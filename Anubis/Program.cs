using System.Threading.RateLimiting;

using k6labs.Anubis.Components;
using k6labs.Anubis.Controllers.Scim;
using k6labs.Anubis.Data;
using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Hosting;
using k6labs.Anubis.Identity;
using k6labs.Anubis.Localization;
using k6labs.Anubis.Options;
using k6labs.Anubis.Scim;
using k6labs.Anubis.Security;
using k6labs.Anubis.Services;
using k6labs.Anubis.Services.Activity;
using k6labs.Anubis.Services.Avatar;
using k6labs.Anubis.Services.Email;
using k6labs.Anubis.Services.Sms;
using k6labs.Anubis.Telemetry;
using k6labs.Anubis.Tenancy;
using k6labs.Hathor.Lucide;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

using static OpenIddict.Abstractions.OpenIddictConstants;

var builder = WebApplication.CreateBuilder(args);

var services = builder.Services;
var environment = builder.Environment;
var configuration = builder.Configuration;

// ---------------------------------------------------------------------------
// Configuration sources, lowest to highest priority:
//   appsettings.json < appsettings.{Environment}.json
//   < Anubis:ConfigFile (optional JSON file, e.g. a Kubernetes ConfigMap)
//   < Anubis:SecretsDirectory (optional key-per-file directory, e.g. a mounted Secret:
//     a file "Email__Smtp__Password" sets Email:Smtp:Password)
//   < environment variables < command line
//   < the tenant's settings file written by the admin UI (data directory)
// Everything up to the command line is the server's configuration, the same for all tenants.
// ---------------------------------------------------------------------------
ConfigurationSources.AddMountedSources(configuration, environment.ContentRootPath);

// Everything Anubis writes (databases, admin settings, keys) goes to one data directory,
// e.g. /data on a persistent volume in Kubernetes (Anubis:DataDirectory).
var dataPaths = DataPaths.Resolve(configuration, environment.ContentRootPath);
services.AddSingleton(dataPaths);

// ---------------------------------------------------------------------------
// Tenants: each has a SQLite database and a settings file of its own. The tenant of a request
// comes from its host name (TenantMiddleware); the catalog database lists the tenants.
// ---------------------------------------------------------------------------
services.AddDbContextFactory<CatalogDbContext>((provider, options) =>
    options.UseSqlite(dataPaths.CatalogConnectionString(provider.GetRequiredService<IConfiguration>().GetConnectionString("Tenants"))));
services.AddSingleton<TenantCatalog>();
services.AddSingleton<TenantConfigurations>();
services.AddSingleton<TenantAdministration>();
services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, TenantCircuitHandler>();

// Settings edited in the admin UI live in a JSON file per tenant in the data directory, the last
// source of the tenant's configuration, so it wins over appsettings.json and environment variables.
services.AddTransient(provider => provider.GetRequiredService<TenantConfigurations>().CurrentSettings);

// Data Protection keys (sign-in cookies, antiforgery tokens, email links, consent) are kept in
// the data directory, so they survive restarts and new pods.
services.AddDataProtection()
    .SetApplicationName("Anubis")
    .PersistKeysToFileSystem(new DirectoryInfo(dataPaths.KeysDirectory));

// Options are per tenant: bound from the tenant's configuration and cached per tenant.
services.AddTenantOptions<AnubisOptions>(AnubisOptions.SectionName)
    // A tenant's public address must be the tenant's: an issuer with another tenant's host name
    // would sign tokens in that tenant's name. Such a value is ignored (the address then comes from each request).
    .PostConfigure<TenantCatalog>((options, catalog) =>
    {
        if (!string.IsNullOrWhiteSpace(options.Issuer)
            && !(Uri.TryCreate(options.Issuer, UriKind.Absolute, out var issuer) && catalog.Resolve(issuer.Host).Id == TenantContext.Current.Id))
        {
            options.Issuer = null;
        }
    });
services.AddTenantOptions<AppearanceOptions>(AppearanceOptions.SectionName);
services.AddTenantOptions<GeneralOptions>(GeneralOptions.SectionName);

// The server's own values, the same for all tenants: the API scope and the default token lifetimes.
var anubisOptions = configuration.GetSection(AnubisOptions.SectionName).Get<AnubisOptions>() ?? new AnubisOptions();

// ---------------------------------------------------------------------------
// Persistence: SQLite via EF Core (Identity + OpenIddict entities), one database per tenant
// ---------------------------------------------------------------------------
// Read when the context is built (not here), so configuration added later (tests) applies, and the
// context of a scope is the current tenant's. A relative Data Source (default "anubis.db") is
// placed in the data directory; the other tenants' files are tenants/{id}/anubis.db.
services.AddDbContext<AnubisDbContext>((provider, options) =>
{
    options.UseSqlite(dataPaths.TenantConnectionString(TenantContext.Current, provider.GetRequiredService<IConfiguration>().GetConnectionString("Default")));
    options.UseOpenIddict<Client, Authorization, Scope, Token, string>();
});

// ---------------------------------------------------------------------------
// Identity: users, groups, cookie authentication, Argon2id password hashing
// ---------------------------------------------------------------------------
services.AddIdentity<User, Group>(options =>
    {
        // Password policy and lockout: see the "Anubis:Security" block below (admin "Security" page).
        options.User.RequireUniqueEmail = true;
        options.Lockout.AllowedForNewUsers = true;

        options.SignIn.RequireConfirmedAccount = false;
        options.Tokens.EmailConfirmationTokenProvider = EmailConfirmationTokenProvider<User>.ProviderName;

        // Schema version 3 adds the UserPasskeys table (WebAuthn / FIDO2).
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    .AddEntityFrameworkStores<AnubisDbContext>()
    .AddUserManager<UserManager>()
    .AddSignInManager<SignInManager>()
    .AddErrorDescriber<LocalizedIdentityErrorDescriber>()
    .AddDefaultTokenProviders()
    .AddTokenProvider<EmailConfirmationTokenProvider<User>>(EmailConfirmationTokenProvider<User>.ProviderName);

// Settings from the admin pages (General / Security), applied without a restart: IdentityOptions
// is per tenant and recomputed whenever a configuration (including a settings file) reloads, and
// the custom UserManager / SignInManager read the current value for each request.
services.AddTenantOptions<SecurityOptions>(SecurityOptions.SectionName);
services.AddTenantOptions<IdentityOptions>()
    .Configure<IOptionsMonitor<AnubisOptions>, IOptionsMonitor<SecurityOptions>>((identity, anubis, securityMonitor) =>
    {
        var security = securityMonitor.CurrentValue;
        identity.SignIn.RequireConfirmedEmail = anubis.CurrentValue.RequireConfirmedEmail;
        identity.Password.RequiredLength = Math.Clamp(security.PasswordMinLength, SecurityOptions.Limits.PasswordMinLengthMin, SecurityOptions.Limits.PasswordMinLengthMax);
        identity.Password.RequireDigit = security.PasswordRequireDigit;
        identity.Password.RequireLowercase = security.PasswordRequireLowercase;
        identity.Password.RequireUppercase = security.PasswordRequireUppercase;
        identity.Password.RequireNonAlphanumeric = security.PasswordRequireNonAlphanumeric;
        identity.Lockout.MaxFailedAccessAttempts = Math.Clamp(security.LockoutMaxFailedAttempts, SecurityOptions.Limits.LockoutAttemptsMin, SecurityOptions.Limits.LockoutAttemptsMax);
        identity.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(Math.Clamp(security.LockoutMinutes, SecurityOptions.Limits.LockoutMinutesMin, SecurityOptions.Limits.LockoutMinutesMax));
    });

// Password reset links (default token provider) are valid for 1 hour and, because
// resetting changes the security stamp, only once.
services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromHours(1));
services.AddOptions<EmailConfirmationTokenProviderOptions>();

// Outgoing email: SMTP by default, or SendGrid / Mailgun / Postmark / Resend (section "Email").
services.AddAnubisEmail();

services.AddScoped<IPasswordHasher<User>, Argon2idPasswordHasher<User>>();
services.AddSingleton<PasswordPolicy>();

// Passkeys (WebAuthn / FIDO2). User verification is required, so every passkey
// sign-in also proves a biometric (fingerprint, face) or device PIN check.
// Passkeys must be discoverable so users can sign in without typing an email.
services.AddTenantOptions<IdentityPasskeyOptions>()
    .Configure<IOptionsMonitor<AnubisOptions>>((options, anubis) =>
    {
        options.UserVerificationRequirement = "required";
        options.ResidentKeyRequirement = "required";

        if (Uri.TryCreate(anubis.CurrentValue.Issuer, UriKind.Absolute, out var issuer))
        {
            // Passkeys are bound to this domain; it must match the tenant's public host name.
            options.ServerDomain = issuer.Host;
        }
    });

services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "anubis.auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.LoginPath = "/login";
    options.AccessDeniedPath = "/access-denied";
    options.ReturnUrlParameter = "returnUrl";
});

// Cookies are per tenant: encrypted for the tenant (TenantCookieProtection), with the session length
// from its "Security" settings; the cookie handler reads its options per request.
services.AddTenantOptions<CookieAuthenticationOptions>();
services.AddSingleton<IConfigureOptions<CookieAuthenticationOptions>, TenantCookieProtection>();
services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
    .Configure<IOptionsMonitor<SecurityOptions>>((cookie, security) =>
    {
        cookie.ExpireTimeSpan = TimeSpan.FromHours(Math.Clamp(security.CurrentValue.SessionLifetimeHours, SecurityOptions.Limits.SessionHoursMin, SecurityOptions.Limits.SessionHoursMax));
        cookie.SlidingExpiration = security.CurrentValue.SlidingSession;
    });

// Localization: the 24 official EU languages (Cultures.Supported). The first provider with an answer wins:
// saved on the account (set on the account page) > OIDC ui_locales > browser language.
services.Configure<RequestLocalizationOptions>(options =>
{
    options.SetDefaultCulture(Cultures.Default)
           .AddSupportedCultures([.. Cultures.Supported])
           .AddSupportedUICultures([.. Cultures.Supported]);
    options.FallBackToParentCultures = true;
    options.FallBackToParentUICultures = true;
    options.RequestCultureProviders =
    [
        new UserLanguageCultureProvider(),
        new UiLocalesCultureProvider(),
        new Microsoft.AspNetCore.Localization.AcceptLanguageHeaderRequestCultureProvider(),
    ];
});

// Emit Czech (and other non-ASCII) text as-is instead of &#x...; entities.
services.Configure<Microsoft.Extensions.WebEncoders.WebEncoderOptions>(options =>
    options.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(System.Text.Unicode.UnicodeRanges.All));

services.AddCascadingAuthenticationState();
services.AddAuthorization();
services.AddHttpContextAccessor();

// ---------------------------------------------------------------------------
// OpenIddict: OIDC/OAuth2 server (authorization code + PKCE, refresh token,
// client credentials) with EF Core stores and validation for the userinfo endpoint
// ---------------------------------------------------------------------------
services.AddOpenIddict()
    .AddCore(options =>
    {
        options.UseEntityFrameworkCore()
               .UseDbContext<AnubisDbContext>()
               .ReplaceDefaultEntities<Client, Authorization, Scope, Token, string>();
    })
    .AddServer(options =>
    {
        options.SetAuthorizationEndpointUris("/authorize")
               .SetEndSessionEndpointUris("/endsession")
               .SetTokenEndpointUris("/token")
               .SetUserInfoEndpointUris("/userinfo")
               .SetIntrospectionEndpointUris("/introspect")
               .SetRevocationEndpointUris("/revocation");

        options.RegisterScopes(Scopes.OpenId, Scopes.Profile, Scopes.Email, GroupClaims.Scope, RoleClaims.Scope, Scopes.OfflineAccess, anubisOptions.ApiScope);

        options.AllowAuthorizationCodeFlow()
               .AllowRefreshTokenFlow()
               .AllowClientCredentialsFlow();

        // PKCE is mandatory for public clients and recommended for all clients.
        options.RequireProofKeyForCodeExchange();

        options.SetAccessTokenLifetime(TimeSpan.FromMinutes(anubisOptions.AccessTokenLifetimeMinutes))
               .SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(anubisOptions.AuthorizationCodeLifetimeMinutes))
               .SetRefreshTokenLifetime(TimeSpan.FromDays(anubisOptions.RefreshTokenLifetimeDays));

        // Signing / encryption certificates of the default tenant: configured (Anubis:Keys) or
        // generated once into the data directory, the same in every environment, so tokens survive
        // restarts. The other tenants have generated ones of their own (below).
        foreach (var certificate in OpenIddictCertificates.Load(configuration, dataPaths, OpenIddictCertificates.Purpose.Signing))
        {
            options.AddSigningCertificate(certificate);
        }

        foreach (var certificate in OpenIddictCertificates.Load(configuration, dataPaths, OpenIddictCertificates.Purpose.Encryption))
        {
            options.AddEncryptionCertificate(certificate);
        }

        var aspNetCore = options.UseAspNetCore()
            .EnableAuthorizationEndpointPassthrough()
            .EnableEndSessionEndpointPassthrough()
            .EnableTokenEndpointPassthrough()
            .EnableUserInfoEndpointPassthrough()
            .EnableStatusCodePagesIntegration();

        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            aspNetCore.DisableTransportSecurityRequirement();
        }
    })
    .AddValidation(options =>
    {
        // Validate access tokens issued by this very server (local validation).
        options.UseLocalServer();
        options.UseAspNetCore();
    });

// The OpenIddict options are per tenant too, for the issuer: the tenant's public address
// (Anubis:Issuer of its configuration) or, without one, the address of each request.
services.AddTenantOptions<OpenIddict.Server.OpenIddictServerOptions>()
    .Configure<IOptionsMonitor<AnubisOptions>>((options, anubis) =>
    {
        if (Uri.TryCreate(anubis.CurrentValue.Issuer, UriKind.Absolute, out var issuer))
        {
            options.Issuer = issuer;
        }

        // Every tenant but the default one signs and encrypts with keys of its own (tenants/{id}/keys),
        // so its tokens are worth nothing anywhere else, whatever their claims say.
        var tenant = TenantContext.Current;
        if (!tenant.IsDefault)
        {
            var keys = dataPaths.TenantKeysDirectory(tenant.Id);
            options.SigningCredentials.Clear();
            options.EncryptionCredentials.Clear();
            foreach (var certificate in OpenIddictCertificates.LoadGenerated(keys, OpenIddictCertificates.Purpose.Signing))
            {
                options.SigningCredentials.Add(new Microsoft.IdentityModel.Tokens.SigningCredentials(
                    new Microsoft.IdentityModel.Tokens.X509SecurityKey(certificate), Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256));
            }

            foreach (var certificate in OpenIddictCertificates.LoadGenerated(keys, OpenIddictCertificates.Purpose.Encryption))
            {
                options.EncryptionCredentials.Add(new Microsoft.IdentityModel.Tokens.EncryptingCredentials(
                    new Microsoft.IdentityModel.Tokens.X509SecurityKey(certificate),
                    Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaOAEP, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.Aes256CbcHmacSha512));
            }
        }
    });
services.AddTenantOptions<OpenIddict.Validation.OpenIddictValidationOptions>();

// ---------------------------------------------------------------------------
// Rate limiting for the token endpoint and the API, per tenant: the traffic of one
// tenant does not use up the limit of another.
// ---------------------------------------------------------------------------
services.AddRateLimiter(limiterOptions =>
{
    limiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiterOptions.AddPolicy("oidc", _ => RateLimitPartition.GetFixedWindowLimiter(TenantContext.Current.Id, _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 30,
        Window = TimeSpan.FromMinutes(1),
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 0,
    }));
    limiterOptions.AddPolicy("api", _ => RateLimitPartition.GetFixedWindowLimiter(TenantContext.Current.Id, _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 120,
        Window = TimeSpan.FromMinutes(1),
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 0,
    }));
});

// ---------------------------------------------------------------------------
// SCIM 2.0 provisioning API under /api/ (RFC 7643 / 7644): a bearer access token
// with the API scope (client credentials grant on /token) may manage users,
// groups and claims.
// ---------------------------------------------------------------------------
services.AddAuthorization(options =>
{
    options.AddPolicy(ScimControllerBase.PolicyName, policy =>
    {
        policy.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
        // The token of one tenant's client is no key to another tenant's API.
        policy.RequireAssertion(context => context.User.HasScope(anubisOptions.ApiScope) && TenantClaims.IsForCurrentTenant(context.User));
    });
});

// ---------------------------------------------------------------------------
// Application services
// ---------------------------------------------------------------------------
services.AddControllersWithViews(options =>
{
    // SCIM payloads arrive and leave as application/scim+json (plain application/json works too).
    foreach (var formatter in options.InputFormatters.OfType<Microsoft.AspNetCore.Mvc.Formatters.SystemTextJsonInputFormatter>())
    {
        formatter.SupportedMediaTypes.Add(ScimConstants.MediaType);
    }

    foreach (var formatter in options.OutputFormatters.OfType<Microsoft.AspNetCore.Mvc.Formatters.SystemTextJsonOutputFormatter>())
    {
        formatter.SupportedMediaTypes.Add(ScimConstants.MediaType);
    }
});
services.AddRazorComponents()
        .AddInteractiveServerComponents();

// Hathor icons are resolved across interactive render-mode boundaries.
services.AddHathorLucide();

// The database of the tenant the probe's host name belongs to (the default one for a bare address).
services.AddHealthChecks()
        .AddDbContextCheck<CatalogDbContext>("tenants")
        .AddDbContextCheck<AnubisDbContext>("database");

// Per request (IOptionsSnapshot), so the consent lifetime from the "Tokens" page applies right away.
services.AddScoped<IConsentService>(provider => new ConsentService(
    provider.GetRequiredService<IDataProtectionProvider>(),
    provider.GetRequiredService<IOptionsSnapshot<AnubisOptions>>()));
services.AddScoped<AvatarService>();
services.AddScoped<UserAdministration>();
services.AddScoped<GroupAdministration>();
services.AddScoped<RoleAdministration>();
services.AddScoped<ScimService>();
services.AddScoped<ScimRoleService>();
services.AddSingleton<ScimExceptionFilter>();

// Phone numbers on the account (verified by an SMS code). LogSmsSender only logs the
// message; register a real ISmsSender to send texts.
services.AddSingleton<ISmsSender, LogSmsSender>();
services.AddScoped<PhoneNumberService>();
services.AddSingleton<AppearanceAssets>();
services.AddScoped<OidcClientService>();
services.AddScoped<OidcScopeService>();
services.AddHostedService<TokenPruningService>();

// Account history (/history): who did what, when, from where.
services.AddScoped<ClientInfo>();
services.AddScoped<AccountActivity>();
services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, ClientInfoCircuitHandler>();

// ---------------------------------------------------------------------------
// OpenTelemetry: logs, traces and metrics. Exported over OTLP when
// OTEL_EXPORTER_OTLP_ENDPOINT is set (plus the other standard OTEL_* variables,
// e.g. OTEL_EXPORTER_OTLP_PROTOCOL, OTEL_EXPORTER_OTLP_HEADERS, OTEL_SERVICE_NAME,
// OTEL_RESOURCE_ATTRIBUTES); without it nothing leaves the process.
// ---------------------------------------------------------------------------
var otel = services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(serviceName: "anubis", serviceVersion: AnubisTelemetry.Version)
        .AddAttributes([new("deployment.environment.name", environment.EnvironmentName)]))
    .WithTracing(tracing => tracing
        .AddSource(AnubisTelemetry.Name)
        .AddSource("MailKit.Net.SmtpClient")
        .AddAspNetCoreInstrumentation(options =>
        {
            // Health probes and static assets only add noise.
            options.Filter = context =>
                !context.Request.Path.StartsWithSegments("/healthz")
                && !context.Request.Path.StartsWithSegments("/_framework")
                && !context.Request.Path.StartsWithSegments("/_content");
        })
        .AddHttpClientInstrumentation()
        .AddEntityFrameworkCoreInstrumentation())
    .WithMetrics(metrics => metrics
        .AddMeter(AnubisTelemetry.Name)
        .AddAspNetCoreInstrumentation()            // http.server.*, kestrel, routing, rate limiting, ...
        .AddMeter("Microsoft.AspNetCore.Authentication", "Microsoft.AspNetCore.Authorization", "Microsoft.AspNetCore.Identity",
                  "Microsoft.AspNetCore.Components", "Microsoft.AspNetCore.Components.Server.Circuits")
        .AddMeter("MailKit.Net.SmtpClient")
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation())
    .WithLogging(logging => { }, options =>
    {
        options.IncludeFormattedMessage = true;
        options.IncludeScopes = true;
    });

if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    otel.UseOtlpExporter();
}

// ---------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------
var app = builder.Build();

// Accounts in all tenants.
AnubisTelemetry.ObserveUsers(() => app.Services.GetRequiredService<TenantCatalog>().All.Sum(tenant =>
{
    using (TenantContext.Use(tenant))
    {
        using var scope = app.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AnubisDbContext>().Users.LongCount();
    }
}));

await DatabaseInitializer.InitializeAsync(app.Services);

// Before everything else: the tenant of the request (by its host name) decides which database,
// settings and appearance the rest of the pipeline sees.
app.UseMiddleware<TenantMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

app.UseSecurityHeaders();
app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// IP address / user agent / user for the account history of this request.
app.Use((context, next) =>
{
    context.RequestServices.GetRequiredService<ClientInfo>().CaptureFrom(context);
    return next(context);
});

// After authentication, so the user's saved language can be read.
app.UseRequestLocalization();

app.UseAntiforgery();

app.MapControllers();
app.MapPasskeyEndpoints();

// Uploaded logo / favicon (data URLs in Anubis:Appearance) and PNG favicons generated from the
// favicon. URLs carry ?v=<content hash>, so a matching request can be cached for good.
static IResult AppearanceImage(HttpContext context, string contentType, byte[] data, string version)
{
    var headers = context.Response.Headers;
    headers.CacheControl = context.Request.Query["v"] == version ? "public, max-age=31536000, immutable" : "no-cache";
    if (contentType == ImageProcessing.SvgContentType)
    {
        // An SVG opened directly must not run scripts in this origin.
        headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; sandbox";
    }

    return Results.File(data, contentType);
}

app.MapGet("/appearance/logo", (HttpContext context, AppearanceAssets assets)
    => assets.LogoData() is { } logo ? AppearanceImage(context, logo.ContentType, logo.Data, assets.LogoVersion) : Results.NotFound());
app.MapGet("/appearance/favicon", (HttpContext context, AppearanceAssets assets)
    => assets.FaviconData() is { } icon ? AppearanceImage(context, icon.ContentType, icon.Data, assets.FaviconVersion) : Results.NotFound());
app.MapGet("/appearance/favicon-{size:int}.png", (HttpContext context, int size, AppearanceAssets assets)
    => assets.PngFavicon(size) is { } png ? AppearanceImage(context, ImageProcessing.PngContentType, png, assets.FaviconVersion) : Results.NotFound());

app.MapHealthChecks("/healthz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
            }),
        });
    },
});
app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();

app.Run();

// Makes the top-level-statements Program class visible to WebApplicationFactory in tests.
namespace k6labs.Anubis
{
    public partial class Program
    {
    }
}
