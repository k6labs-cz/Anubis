using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace k6labs.Anubis.Tenancy;

/// <summary>
/// The sign-in cookies of a tenant (session, two-factor, ...) are encrypted for that tenant only, so a
/// cookie taken from one tenant is not a session in another, even though all share one key ring.
/// The default tenant keeps the server's protector: sessions from before tenants stay valid.
/// </summary>
public sealed class TenantCookieProtection(IDataProtectionProvider dataProtection) : IConfigureNamedOptions<CookieAuthenticationOptions>
{
    public const string Purpose = "Anubis.Tenant";

    public void Configure(string? name, CookieAuthenticationOptions options)
    {
        var tenant = TenantContext.Current;
        if (!tenant.IsDefault)
        {
            options.DataProtectionProvider = dataProtection.CreateProtector(Purpose, tenant.Id);
        }
    }

    public void Configure(CookieAuthenticationOptions options) => Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
}
