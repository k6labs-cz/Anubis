using k6labs.Anubis.Options;
using k6labs.Anubis.Services;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Anubis.Tests;

public class ConsentServiceTests
{
    private static ConsentService CreateService()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        var provider = services.BuildServiceProvider();

        return new ConsentService(
            provider.GetRequiredService<IDataProtectionProvider>(),
            Microsoft.Extensions.Options.Options.Create(new AnubisOptions()));
    }

    [Fact]
    public void GrantedConsent_IsValidForSameUserClientAndScopes()
    {
        var service = CreateService();
        var grantContext = new DefaultHttpContext();

        service.GrantConsent(grantContext, "user-1", "client-a", new[] { "openid", "profile" });

        var cookie = grantContext.Response.Headers.SetCookie.ToString();
        Assert.Contains(ConsentService.CookieName, cookie);

        var requestContext = new DefaultHttpContext();
        requestContext.Request.Headers.Cookie = cookie;

        Assert.True(service.HasConsent(requestContext, "user-1", "client-a", new[] { "profile", "openid" }));
    }

    [Fact]
    public void Consent_IsRejectedForDifferentScopesUserOrClient()
    {
        var service = CreateService();
        var grantContext = new DefaultHttpContext();

        service.GrantConsent(grantContext, "user-1", "client-a", new[] { "openid" });

        var cookie = grantContext.Response.Headers.SetCookie.ToString();

        DefaultHttpContext RequestWithCookie()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers.Cookie = cookie;
            return context;
        }

        Assert.False(service.HasConsent(RequestWithCookie(), "user-1", "client-a", new[] { "openid", "email" }));
        Assert.False(service.HasConsent(RequestWithCookie(), "user-2", "client-a", new[] { "openid" }));
        Assert.False(service.HasConsent(RequestWithCookie(), "user-1", "client-b", new[] { "openid" }));
    }

    [Fact]
    public void TamperedConsentCookie_IsRejected()
    {
        var service = CreateService();
        var grantContext = new DefaultHttpContext();

        service.GrantConsent(grantContext, "user-1", "client-a", new[] { "openid" });

        var cookie = grantContext.Response.Headers.SetCookie.ToString();
        var value = cookie.Split(';')[0].Split('=', 2)[1];
        var tampered = value[..^4] + "AAAA";

        var requestContext = new DefaultHttpContext();
        requestContext.Request.Headers.Cookie = $"{ConsentService.CookieName}={tampered}";

        Assert.False(service.HasConsent(requestContext, "user-1", "client-a", new[] { "openid" }));
    }

    [Fact]
    public void MissingConsentCookie_IsRejected()
    {
        var service = CreateService();

        Assert.False(service.HasConsent(new DefaultHttpContext(), "user-1", "client-a", new[] { "openid" }));
    }
}
