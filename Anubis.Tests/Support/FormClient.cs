using System.Net;
using System.Text.RegularExpressions;

using k6labs.Anubis.Data.Entities;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Anubis.Tests.Support;

/// <summary>
/// Drives the statically rendered Blazor forms over HTTP like a browser would:
/// keeps cookies, reads the antiforgery token from a page and posts form fields.
/// </summary>
public sealed partial class FormClient : IDisposable
{
    private readonly AnubisFactory _factory;

    /// <param name="factory">The application factory used to create the HTTP client.</param>
    /// <param name="language">Sent as Accept-Language, like a browser set to that language.</param>
    public FormClient(AnubisFactory factory, string? language = null)
    {
        _factory = factory;
        Http = factory.CreateAnonymousClient();
        if (language is not null)
        {
            Http.DefaultRequestHeaders.AcceptLanguage.ParseAdd(language);
        }

        // Browsers send Origin on form posts; passkey verification compares it with the WebAuthn origin.
        Http.DefaultRequestHeaders.Add("Origin", "http://localhost");
    }

    public HttpClient Http { get; }

    /// <summary>Loads a page and returns its antiforgery token.</summary>
    public async Task<string> GetTokenAsync(string url)
    {
        var html = await Http.GetStringAsync(url);
        return ReadToken(html);
    }

    public static string ReadToken(string html)
    {
        var match = TokenRegex().Match(html);
        Assert.True(match.Success, "No antiforgery token found on the page.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    /// <summary>Posts a Blazor form (identified by its FormName) with the page's token.</summary>
    public async Task<HttpResponseMessage> PostFormAsync(string url, string formName, IDictionary<string, string> fields)
    {
        var token = await GetTokenAsync(url);
        var content = new Dictionary<string, string>(fields)
        {
            ["__RequestVerificationToken"] = token,
            ["_handler"] = formName,
        };
        return await Http.PostAsync(url, new FormUrlEncodedContent(content));
    }

    public Task<HttpResponseMessage> PasswordLoginAsync(string email, string password, string returnUrl = "/profile")
        => PostFormAsync($"/login?returnUrl={Uri.EscapeDataString(returnUrl)}", "login", new Dictionary<string, string>
        {
            ["model.Email"] = email,
            ["model.Password"] = password,
        });

    public async Task SignOutAsync()
    {
        var token = await GetTokenAsync("/profile");
        using var response = await Http.PostAsync("/auth/logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    /// <summary>True when the auth cookie lets the client open a protected page.</summary>
    public async Task<bool> IsSignedInAsync()
    {
        using var response = await Http.GetAsync("/profile");
        return response.StatusCode == HttpStatusCode.OK;
    }

    /// <summary>Creates a fresh user so tests don't share MFA state.</summary>
    public async Task<(User User, string Password)> CreateUserAsync()
    {
        const string password = "Test-User-Password-123!";
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var email = $"user-{Guid.NewGuid():N}@test.local";
        var user = new User { UserName = email, Email = email, EmailConfirmed = true };
        var result = await userManager.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));
        return (user, password);
    }

    public void Dispose() => Http.Dispose();

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();
}
