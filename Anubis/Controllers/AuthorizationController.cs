using System.Collections.Immutable;
using System.Security.Claims;

using k6labs.Anubis.Data;
using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Identity;
using k6labs.Anubis.Services;
using k6labs.Anubis.Services.Avatar;
using k6labs.Anubis.Tenancy;

using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;

using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;

using static OpenIddict.Abstractions.OpenIddictConstants;

namespace k6labs.Anubis.Controllers;

/// <summary>
/// OpenID Connect server endpoints implemented with OpenIddict:
/// authorization, token, userinfo and end-session.
/// </summary>
public class AuthorizationController : Controller
{
    private readonly IOpenIddictApplicationManager _applicationManager;
    private readonly IOpenIddictScopeManager _scopeManager;
    private readonly SignInManager<User> _signInManager;
    private readonly UserManager<User> _userManager;
    private readonly IConsentService _consentService;
    private readonly RoleManager<Group> _groupManager;
    private readonly AnubisDbContext _db;

    public AuthorizationController(
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictScopeManager scopeManager,
        SignInManager<User> signInManager,
        UserManager<User> userManager,
        IConsentService consentService,
        RoleManager<Group> groupManager,
        AnubisDbContext db)
    {
        _db = db;
        _groupManager = groupManager;
        _applicationManager = applicationManager;
        _scopeManager = scopeManager;
        _signInManager = signInManager;
        _userManager = userManager;
        _consentService = consentService;
    }

    [HttpGet("~/authorize")]
    [HttpPost("~/authorize")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Authorize()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        // Retrieve the user principal stored in the application cookie.
        var result = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);

        // If the user is not authenticated yet, redirect them to the login page.
        if (result is null || !result.Succeeded)
        {
            return Challenge(
                authenticationSchemes: IdentityConstants.ApplicationScheme,
                properties: new AuthenticationProperties
                {
                    RedirectUri = Request.PathBase + Request.Path + QueryString.Create(
                        Request.HasFormContentType
                            ? Request.Form.ToList()
                            : Request.Query.ToList()),
                });
        }

        var user = await _userManager.GetUserAsync(result.Principal)
            ?? throw new InvalidOperationException("The user details cannot be retrieved.");

        // For interactive authorization code requests, make sure the user consented to this client.
        if (request.HasResponseType(ResponseTypes.Code))
        {
            var application = await _applicationManager.FindByClientIdAsync(request.ClientId!);
            var requireConsent = application is Client client && client.RequireConsent;

            if (requireConsent && !_consentService.HasConsent(HttpContext, user.Id, request.ClientId!, request.GetScopes()))
            {
                var returnUrl = (string)(Request.PathBase + Request.Path + Request.QueryString);
                return Redirect("/consent?returnUrl=" + Uri.EscapeDataString(returnUrl));
            }
        }

        var principal = await CreatePrincipalAsync(user, request.GetScopes(), request.ClientId);

        // History: "Signed in to <application>", and the code issued for it.
        var app = await _applicationManager.FindByClientIdAsync(request.ClientId!);
        var appName = app is null ? request.ClientId : await _applicationManager.GetDisplayNameAsync(app) ?? request.ClientId;
        await HttpContext.RequestServices.GetRequiredService<Services.Activity.AccountActivity>()
            .RecordAsync(user.Id, Services.Activity.UserEventTypes.ApplicationSignIn, appName);
        CountIssued("authorization_code", request.ClientId);

        return SignIn(WithLifetimes(principal), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Token lifetimes from the "Tokens" settings, set on each principal so a change applies to
    /// the next token without a restart (OpenIddict's server-wide defaults are only the fallback).
    /// </summary>
    private ClaimsPrincipal WithLifetimes(ClaimsPrincipal principal)
    {
        var options = HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Options.AnubisOptions>>().CurrentValue;
        principal.SetAccessTokenLifetime(TimeSpan.FromMinutes(options.AccessTokenLifetimeMinutes));
        principal.SetIdentityTokenLifetime(TimeSpan.FromMinutes(options.IdentityTokenLifetimeMinutes));
        principal.SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(options.AuthorizationCodeLifetimeMinutes));
        principal.SetRefreshTokenLifetime(TimeSpan.FromDays(options.RefreshTokenLifetimeDays));
        return principal;
    }

    /// <summary>Counts a response that issued a code or tokens (metric <c>anubis.tokens.issued</c>).</summary>
    private static void CountIssued(string? grantType, string? clientId)
        => Telemetry.AnubisTelemetry.TokensIssued.Add(1,
            new KeyValuePair<string, object?>("oauth.grant_type", grantType ?? "unknown"),
            new KeyValuePair<string, object?>("oauth.client_id", clientId ?? "unknown"));

    [HttpPost("~/token")]
    [EnableRateLimiting("oidc")]
    [IgnoreAntiforgeryToken]
    [Produces("application/json")]
    public async Task<IActionResult> Exchange()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            // Retrieve the claims principal stored in the authorization code / refresh token.
            var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

            if (result.Principal is null)
            {
                throw new InvalidOperationException("The token details cannot be retrieved.");
            }

            // The code / refresh token carries the user id as "sub" (not ASP.NET Identity's name identifier claim).
            var user = await _userManager.FindByIdAsync(result.Principal.GetClaim(Claims.Subject) ?? string.Empty);
            if (user is null)
            {
                return Forbid(
                    authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                    properties: new AuthenticationProperties(new Dictionary<string, string?>
                    {
                        [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
                        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The account no longer exists.",
                    }));
            }

            // The scopes granted with the code / refresh token; a token request names none (or, when refreshing, fewer).
            var granted = result.Principal.GetScopes();
            var scopes = request.GetScopes() is { Length: > 0 } asked ? asked.Intersect(granted, StringComparer.Ordinal).ToImmutableArray() : granted;

            // Rebuild the principal with fresh claims (groups, roles or profile may have changed since the code was issued).
            var principal = await CreatePrincipalAsync(user, scopes, request.ClientId);

            CountIssued(request.GrantType, request.ClientId);
            return SignIn(WithLifetimes(principal), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        if (request.IsClientCredentialsGrantType())
        {
            var application = await _applicationManager.FindByClientIdAsync(request.ClientId!)
                ?? throw new InvalidOperationException("The application details cannot be found.");

            var identity = new ClaimsIdentity(
                authenticationType: TokenValidationParameters.DefaultAuthenticationType,
                nameType: Claims.Name,
                roleType: GroupClaims.Claim);

            identity.SetClaim(Claims.Subject, await _applicationManager.GetClientIdAsync(application));
            identity.SetClaim(Claims.ClientId, await _applicationManager.GetClientIdAsync(application));
            identity.SetClaim(TenantClaims.Claim, TenantContext.Current.Id);

            identity.SetScopes(request.GetScopes());
            identity.SetResources(await _scopeManager.ListResourcesAsync(identity.GetScopes()).ToListAsync());

            foreach (var claim in identity.Claims)
            {
                claim.SetDestinations(Destinations.AccessToken);
            }

            CountIssued(request.GrantType, request.ClientId);
            return SignIn(WithLifetimes(new ClaimsPrincipal(identity)), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        throw new InvalidOperationException("The specified grant type is not supported.");
    }

    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
    [HttpGet("~/userinfo")]
    [Produces("application/json")]
    public async Task<IActionResult> Userinfo()
    {
        // An access token of another tenant is not this tenant's, whoever its subject is.
        var user = TenantClaims.IsForCurrentTenant(User)
            ? await _userManager.FindByIdAsync(User.GetClaim(Claims.Subject) ?? string.Empty)
            : null;
        if (user is null)
        {
            return Challenge(
                authenticationSchemes: OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
                properties: new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictValidationAspNetCoreConstants.Properties.Error] = Errors.InvalidToken,
                    [OpenIddictValidationAspNetCoreConstants.Properties.ErrorDescription] = "The specified access token is invalid.",
                }));
        }

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [Claims.Subject] = user.Id,
            [TenantClaims.Claim] = TenantContext.Current.Id,
        };

        if (User.HasScope(Scopes.Profile))
        {
            claims[Claims.Name] = user.DisplayName ?? user.UserName ?? string.Empty;
            claims[Claims.PreferredUsername] = user.UserName ?? string.Empty;
            claims[Claims.Picture] = AvatarUrl.For(user);
        }

        if (User.HasScope(Scopes.Email))
        {
            claims[Claims.Email] = user.Email ?? string.Empty;
            claims[Claims.EmailVerified] = user.EmailConfirmed;
        }

        if (User.HasScope(GroupClaims.Scope))
        {
            claims[GroupClaims.Claim] = await _userManager.GetRolesAsync(user);
        }

        // The roles in the application the access token was issued to.
        if (User.HasScope(RoleClaims.Scope) && User.GetClaim(Claims.ClientId) is { Length: > 0 } clientId)
        {
            claims[RoleClaims.Claim] = await RoleClaims.ForUserAsync(_db, user.Id, clientId);
        }

        // Claims set by an administrator on the user's groups and on the user.
        foreach (var (type, values) in await CustomClaims.ForUserAsync(_userManager, _groupManager, user))
        {
            claims.TryAdd(type, values.Count == 1 ? values[0] : values);
        }

        return Ok(claims);
    }

    [HttpGet("~/endsession")]
    [HttpPost("~/endsession")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> EndSession()
    {
        var request = HttpContext.GetOpenIddictServerRequest();

        // Clear the application authentication cookie.
        await _signInManager.SignOutAsync();

        return SignOut(
            authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            properties: new AuthenticationProperties
            {
                RedirectUri = string.IsNullOrEmpty(request?.PostLogoutRedirectUri) ? "/" : request.PostLogoutRedirectUri,
            });
    }

    private async Task<ClaimsPrincipal> CreatePrincipalAsync(User user, IReadOnlyCollection<string> scopes, string? clientId)
    {
        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: GroupClaims.Claim);

        identity.SetClaim(Claims.Subject, await _userManager.GetUserIdAsync(user))
                .SetClaim(Claims.Email, await _userManager.GetEmailAsync(user))
                .SetClaim(Claims.Name, user.DisplayName ?? await _userManager.GetUserNameAsync(user))
                .SetClaim(Claims.PreferredUsername, await _userManager.GetUserNameAsync(user))
                .SetClaim(Claims.Picture, AvatarUrl.For(user))
                .SetClaim(TenantClaims.Claim, TenantContext.Current.Id)
                .SetClaims(GroupClaims.Claim, (await _userManager.GetRolesAsync(user)).ToImmutableArray());

        // Claims set by an administrator on the user's groups and on the user (reserved types are refused when saving).
        foreach (var (type, values) in await CustomClaims.ForUserAsync(_userManager, _groupManager, user))
        {
            identity.SetClaims(type, values.ToImmutableArray());
        }

        identity.SetScopes(scopes);
        var resources = await _scopeManager.ListResourcesAsync(identity.GetScopes()).ToListAsync();

        // The "roles" scope: the user's roles in the asking application, in a token issued for it.
        if (clientId is not null && identity.HasScope(RoleClaims.Scope))
        {
            identity.SetClaims(RoleClaims.Claim, (await RoleClaims.ForUserAsync(_db, user.Id, clientId)).ToImmutableArray());
            if (!resources.Contains(clientId, StringComparer.Ordinal))
            {
                resources.Add(clientId);
            }
        }

        identity.SetResources(resources);

        foreach (var claim in identity.Claims)
        {
            claim.SetDestinations(GetDestinations(claim, identity));
        }

        return new ClaimsPrincipal(identity);
    }

    private static IEnumerable<string> GetDestinations(Claim claim, ClaimsIdentity identity)
    {
        // The subject claim is always included in the access and identity tokens.
        if (claim.Type == Claims.Subject)
        {
            yield return Destinations.AccessToken;
            yield return Destinations.IdentityToken;
            yield break;
        }

        // Claims are only included in the access token by default.
        yield return Destinations.AccessToken;

        switch (claim.Type)
        {
            case Claims.Name:
            case Claims.PreferredUsername:
            case Claims.Picture:
                if (identity.HasScope(Scopes.Profile))
                {
                    yield return Destinations.IdentityToken;
                }

                yield break;

            case Claims.Email:
                if (identity.HasScope(Scopes.Email))
                {
                    yield return Destinations.IdentityToken;
                }

                yield break;

            case GroupClaims.Claim:
                if (identity.HasScope(GroupClaims.Scope))
                {
                    yield return Destinations.IdentityToken;
                }

                yield break;

            case RoleClaims.Claim:
                // Present only with the "roles" scope.
                yield return Destinations.IdentityToken;
                yield break;

            case TenantClaims.Claim:
                // Which tenant signed the user in: always, next to the subject.
                yield return Destinations.IdentityToken;
                yield break;

            default:
                // Custom claims (CustomClaims): in the ID token too, so clients can read them without userinfo.
                if (!CustomClaims.IsReserved(claim.Type))
                {
                    yield return Destinations.IdentityToken;
                }

                yield break;
        }
    }
}
