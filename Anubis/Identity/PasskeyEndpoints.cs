using k6labs.Anubis.Data.Entities;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace k6labs.Anubis.Identity;

/// <summary>
/// HTTP endpoints called by <c>wwwroot/js/passkey.js</c> to fetch WebAuthn options.
/// They must be plain HTTP endpoints (not Blazor circuit code), because
/// <see cref="SignInManager{TUser}"/> keeps the WebAuthn challenge in a cookie.
/// </summary>
public static class PasskeyEndpoints
{
    public static IEndpointRouteBuilder MapPasskeyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/auth/passkey");

        // Options for registering a new passkey on the signed-in account.
        group.MapPost("/creation-options", async (
            HttpContext context,
            [FromServices] UserManager<User> userManager,
            [FromServices] SignInManager<User> signInManager,
            [FromServices] IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);

            var user = await userManager.GetUserAsync(context.User);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var userName = await userManager.GetUserNameAsync(user) ?? "user";
            var optionsJson = await signInManager.MakePasskeyCreationOptionsAsync(new()
            {
                Id = await userManager.GetUserIdAsync(user),
                Name = userName,
                DisplayName = user.DisplayName ?? userName,
            });

            return Results.Content(optionsJson, "application/json");
        }).RequireAuthorization();

        // Options for signing in. No user is passed, so the browser offers any
        // discoverable passkey for this site and the server never reveals
        // whether an email address has an account.
        group.MapPost("/request-options", async (
            HttpContext context,
            [FromServices] SignInManager<User> signInManager,
            [FromServices] IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);

            var optionsJson = await signInManager.MakePasskeyRequestOptionsAsync(user: null);
            return Results.Content(optionsJson, "application/json");
        });

        return endpoints;
    }
}
