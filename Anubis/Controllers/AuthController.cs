using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Services;
using k6labs.Anubis.Services.Avatar;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace k6labs.Anubis.Controllers;

/// <summary>
/// Cookie-based authentication endpoints used by the Blazor UI
/// (sign-out and the consent decision during the authorization code flow).
/// </summary>
public class AuthController : Controller
{
    private readonly UserManager<User> _userManager;
    private readonly SignInManager<User> _signInManager;
    private readonly IConsentService _consentService;
    private readonly AvatarService _avatarService;

    public AuthController(
        UserManager<User> userManager,
        SignInManager<User> signInManager,
        IConsentService consentService,
        AvatarService avatarService)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _consentService = consentService;
        _avatarService = avatarService;
    }

    [AllowAnonymous]
    [HttpGet("/avatars/{userId}")]
    public async Task<IActionResult> GetAvatar(string userId, CancellationToken cancellationToken)
    {
        var avatar = await _avatarService.GetAvatarAsync(userId, cancellationToken);
        if (avatar is null || avatar.Data.Length == 0)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = Request.Query.ContainsKey("v")
            ? "public, max-age=31536000, immutable"
            : "public, max-age=300";

        return File(avatar.Data, avatar.ContentType);
    }

    [Authorize]
    [HttpPost("/auth/profile")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateProfile([FromForm] string? displayName)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var trimmed = displayName?.Trim();
        if (trimmed is { Length: > 100 })
        {
            return Redirect("/profile?status=profile-invalid");
        }

        var name = string.IsNullOrWhiteSpace(trimmed) ? user.Email : trimmed;
        if (name != user.DisplayName)
        {
            user.DisplayName = name;
            await _userManager.UpdateAsync(user);
            await HttpContext.RequestServices.GetRequiredService<Services.Activity.AccountActivity>()
                .RecordAsync(user.Id, Services.Activity.UserEventTypes.ProfileUpdated, name);
        }

        return Redirect("/profile?status=profile-updated");
    }

    [Authorize]
    [HttpPost("/auth/avatar")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(AvatarService.MaxUploadBytes + 1024 * 64)]
    public async Task<IActionResult> UploadAvatar(IFormFile? avatar, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        if (avatar is null || avatar.Length <= 0)
        {
            return Redirect("/profile?status=avatar-invalid");
        }

        if (avatar.Length > AvatarService.MaxUploadBytes)
        {
            return Redirect("/profile?status=avatar-too-large");
        }

        try
        {
            using var memory = new MemoryStream((int)avatar.Length);
            await avatar.CopyToAsync(memory, cancellationToken);
            await _avatarService.SetAvatarAsync(user, memory.ToArray(), cancellationToken);
            return Redirect("/profile?status=avatar-updated");
        }
        catch (InvalidDataException)
        {
            return Redirect("/profile?status=avatar-invalid");
        }
    }

    [Authorize]
    [HttpPost("/auth/avatar/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAvatar(CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        await _avatarService.RemoveAvatarAsync(user, cancellationToken);
        return Redirect("/profile?status=avatar-removed");
    }

    [HttpPost("/auth/logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        return Redirect("/");
    }

    [Authorize]
    [HttpPost("/auth/consent")]
    [ValidateAntiForgeryToken]
    public IActionResult Consent(string returnUrl, string decision)
    {
        if (!IsLocalAuthorizeUrl(returnUrl))
        {
            return BadRequest(Resources.Translations.Consent_RequestMissing);
        }

        var query = QueryHelpers.ParseQuery(Components.Shared.LocalUrl.QueryOf(returnUrl));
        var clientId = query["client_id"].ToString();
        var scopes = query["scope"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (decision == "deny")
        {
            // Redirect back to the client with an access_denied error (RFC 6749, section 4.1.2.1).
            var errorParameters = new Dictionary<string, string?>
            {
                ["error"] = "access_denied",
                ["error_description"] = "The user denied the authorization request.",
            };

            var state = query["state"].ToString();
            if (!string.IsNullOrEmpty(state))
            {
                errorParameters["state"] = state;
            }

            return Redirect(QueryHelpers.AddQueryString(query["redirect_uri"].ToString(), errorParameters));
        }

        var userId = _userManager.GetUserId(User);
        if (string.IsNullOrEmpty(userId))
        {
            return Challenge();
        }

        _consentService.GrantConsent(HttpContext, userId, clientId, scopes);

        return Redirect(returnUrl);
    }

    private static bool IsLocalAuthorizeUrl(string? returnUrl)
        => !string.IsNullOrEmpty(returnUrl)
            && returnUrl.StartsWith("/authorize", StringComparison.Ordinal)
            && !returnUrl.StartsWith("//", StringComparison.Ordinal);
}
