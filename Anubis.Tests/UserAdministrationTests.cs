using Anubis.Tests.Support;

using k6labs.Anubis.Data;
using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Models;
using k6labs.Anubis.Services;
using k6labs.Anubis.Services.Activity;
using k6labs.Anubis.Services.Avatar;
using k6labs.Anubis.Services.Email;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Anubis.Tests;

/// <summary>
/// Admin edit of accounts (email = sign-in name, display name, phone, roles): validation
/// before anything changes, all-or-nothing saving, and the administrator safety rules.
/// Runs on its own database, so "the only administrator" is known.
/// </summary>
public class UserAdministrationTests : IClassFixture<UserAdministrationTests.Factory>
{
    private const string Password = "Edit-Test-Password-123!";
    private readonly Factory _factory;

    public UserAdministrationTests(Factory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("missing@")]
    [InlineData("two words@example.com")]
    [InlineData("Jane Doe <jane@example.com>")]
    public async Task InvalidEmail_IsRejected_AndNothingChanges(string email)
    {
        var user = await CreateUserAsync();
        using var scope = _factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<UserAdministration>();
        var model = (await admin.LoadAsync(user.Id))!;
        model.Email = email;
        model.DisplayName = "Should not be saved";

        var result = await admin.UpdateAsync(model, actingUserId: null);

        Assert.False(result.Succeeded);
        Assert.True(result.Errors.ContainsKey(nameof(UserEditModel.Email)));
        var saved = await ReloadAsync(user.Id);
        Assert.Equal(user.Email, saved.Email);
        Assert.NotEqual("Should not be saved", saved.DisplayName);
    }

    [Fact]
    public async Task TooLongEmail_AndEmailOfAnotherAccount_AreRejected()
    {
        var user = await CreateUserAsync();
        var other = await CreateUserAsync();
        using var scope = _factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<UserAdministration>();

        var model = (await admin.LoadAsync(user.Id))!;
        model.Email = new string('a', 250) + "@example.com";
        Assert.Equal("Enter a valid email address.", (await admin.UpdateAsync(model, null)).Errors[nameof(UserEditModel.Email)]);

        model.Email = other.Email!.ToUpperInvariant();   // uniqueness ignores case
        Assert.Equal("This email address is already used by another account.", (await admin.UpdateAsync(model, null)).Errors[nameof(UserEditModel.Email)]);
    }

    [Fact]
    public async Task ChangingTheEmail_ChangesTheSignInName_AndIsRecorded()
    {
        var user = await CreateUserAsync();
        var newEmail = $"renamed-{Guid.NewGuid():N}@test.local";
        string stampBefore;
        using (var scope = _factory.Services.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<UserAdministration>();
            var model = (await admin.LoadAsync(user.Id))!;
            stampBefore = (await ReloadAsync(user.Id)).SecurityStamp!;
            model.Email = $"  {newEmail}  ";
            model.EmailConfirmed = true;
            model.DisplayName = "Renamed User";
            model.PhoneNumber = "+420 777 000 111";

            var result = await admin.UpdateAsync(model, actingUserId: null);
            Assert.True(result.Succeeded, string.Join(" ", result.Errors.Values));
        }

        var saved = await ReloadAsync(user.Id);
        Assert.Equal(newEmail, saved.Email);
        Assert.Equal(newEmail, saved.UserName);
        Assert.True(saved.EmailConfirmed);                 // the admin's choice, although SetEmail clears it
        Assert.Equal("Renamed User", saved.DisplayName);
        Assert.Equal("+420777000111", saved.PhoneNumber);
        Assert.False(saved.PhoneNumberConfirmed);           // a changed number must be verified again
        Assert.NotEqual(stampBefore, saved.SecurityStamp);  // old sessions are invalidated

        using var client = new FormClient(_factory);
        Assert.False(await SignInAsync(client, user.Email!));
        Assert.True(await SignInAsync(client, newEmail));
        Assert.Contains(UserEventTypes.EmailChanged, await EventTypesAsync(user.Id));
    }

    [Fact]
    public async Task NewEmail_NotMarkedConfirmed_GetsAConfirmationEmail()
    {
        var user = await CreateUserAsync();
        var unconfirmed = $"to-confirm-{Guid.NewGuid():N}@test.local";
        var vouched = $"vouched-{Guid.NewGuid():N}@test.local";
        using var scope = _factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<UserAdministration>();

        var model = (await admin.LoadAsync(user.Id))!;
        model.Email = unconfirmed;
        model.EmailConfirmed = false;
        var result = await admin.UpdateAsync(model, null, "http://localhost/");

        Assert.Equal(AccountEmailResult.Sent, result.ConfirmationEmail);
        var message = Assert.Single(_factory.Emails.To(unconfirmed));
        Assert.Equal("Confirm your email address", message.Subject);
        Assert.Contains("/confirm-email?", message.TextBody);
        Assert.False((await ReloadAsync(user.Id)).EmailConfirmed);

        // The admin vouches for the address: no email.
        model = (await admin.LoadAsync(user.Id))!;
        model.Email = vouched;
        model.EmailConfirmed = true;
        Assert.Null((await admin.UpdateAsync(model, null, "http://localhost/")).ConfirmationEmail);
        Assert.Empty(_factory.Emails.To(vouched));

        // Saving other fields with an unchanged email sends nothing.
        model = (await admin.LoadAsync(user.Id))!;
        model.EmailConfirmed = false;
        model.DisplayName = "Only the name";
        Assert.Null((await admin.UpdateAsync(model, null, "http://localhost/")).ConfirmationEmail);
        Assert.Empty(_factory.Emails.To(vouched));
    }

    [Fact]
    public async Task Roles_AreAssigned_UserRoleStays_AndUnknownRolesAreRejected()
    {
        var user = await CreateUserAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<RoleManager<Role>>().CreateAsync(new Role("editors"));
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<UserAdministration>();
            var model = (await admin.LoadAsync(user.Id))!;
            model.Roles = ["editors"];                       // "User" not ticked: it is kept anyway
            Assert.True((await admin.UpdateAsync(model, null)).Succeeded);

            model = (await admin.LoadAsync(user.Id))!;
            Assert.Equal(new HashSet<string> { "editors", DatabaseInitializer.UserRole }, model.Roles);

            model.Roles = ["no-such-role"];
            var rejected = await admin.UpdateAsync(model, null);
            Assert.True(rejected.Errors.ContainsKey(nameof(UserEditModel.Roles)));
            Assert.Contains("editors", (await admin.LoadAsync(user.Id))!.Roles);   // unchanged

            model = (await admin.LoadAsync(user.Id))!;
            model.Roles = [];
            Assert.True((await admin.UpdateAsync(model, null)).Succeeded);
            Assert.Equal(new HashSet<string> { DatabaseInitializer.UserRole }, (await admin.LoadAsync(user.Id))!.Roles);
        }

        var types = await EventTypesAsync(user.Id);
        Assert.Contains(UserEventTypes.RoleAdded, types);
        Assert.Contains(UserEventTypes.RoleRemoved, types);
    }

    [Fact]
    public async Task AdminRole_CannotBeRemoved_FromYourself_OrFromTheLastAdministrator()
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var admin = scope.ServiceProvider.GetRequiredService<UserAdministration>();
        var onlyAdmin = (await users.GetUsersInRoleAsync(DatabaseInitializer.AdminRole)).Single();

        var model = (await admin.LoadAsync(onlyAdmin.Id))!;
        model.Roles.Remove(DatabaseInitializer.AdminRole);
        Assert.Equal("You can't remove the Admin role from your own account.", (await admin.UpdateAsync(model, onlyAdmin.Id)).Errors["Roles"]);
        Assert.Equal("The last administrator cannot be removed.", (await admin.UpdateAsync(model, actingUserId: "someone-else")).Errors["Roles"]);
        Assert.True(await users.IsInRoleAsync(onlyAdmin, DatabaseInitializer.AdminRole));
    }

    [Fact]
    public async Task ConcurrentChange_IsNotOverwritten()
    {
        var user = await CreateUserAsync();
        using var scope = _factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<UserAdministration>();
        var stale = (await admin.LoadAsync(user.Id))!;

        var fresh = (await admin.LoadAsync(user.Id))!;
        fresh.DisplayName = "First save";
        Assert.True((await admin.UpdateAsync(fresh, null)).Succeeded);

        stale.DisplayName = "Second save";
        Assert.Equal("The record was changed by someone else. Reload and try again.", (await admin.UpdateAsync(stale, null)).Errors[string.Empty]);
        Assert.Equal("First save", (await ReloadAsync(user.Id)).DisplayName);
    }

    [Fact]
    public async Task InvalidPhoneNumber_IsRejected()
    {
        var user = await CreateUserAsync();
        using var scope = _factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<UserAdministration>();
        var model = (await admin.LoadAsync(user.Id))!;
        model.PhoneNumber = "123";

        Assert.True((await admin.UpdateAsync(model, null)).Errors.ContainsKey(nameof(UserEditModel.PhoneNumber)));
    }

    [Fact]
    public async Task Create_AssignsTheChosenRoles_AndValidatesTheEmail()
    {
        using var scope = _factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<UserAdministration>();
        var existing = await CreateUserAsync();

        var duplicate = await admin.CreateAsync(new CreateUserModel { Email = existing.Email!, Password = Password });
        Assert.True(duplicate.Errors.ContainsKey(nameof(UserEditModel.Email)));

        var email = $"new-admin-{Guid.NewGuid():N}@test.local";
        var created = await admin.CreateAsync(new CreateUserModel { Email = email, Password = Password, Roles = [DatabaseInitializer.AdminRole] });
        Assert.True(created.Succeeded, string.Join(" ", created.Errors.Values));
        var roles = await scope.ServiceProvider.GetRequiredService<UserManager<User>>().GetRolesAsync(created.User!);
        Assert.Equal(new HashSet<string> { DatabaseInitializer.AdminRole, DatabaseInitializer.UserRole }, roles.ToHashSet());

        // Clean up so the "only administrator" test stays valid.
        await scope.ServiceProvider.GetRequiredService<UserManager<User>>().DeleteAsync(created.User!);
    }

    [Fact]
    public async Task UsersPage_OffersEdit_InsteadOfMakeAdmin()
    {
        using var client = new FormClient(_factory);
        (await client.PasswordLoginAsync(AnubisFactory.AdminEmail, AnubisFactory.AdminPassword)).Dispose();

        var html = await client.Http.GetStringAsync("/admin/users");

        Assert.Contains("data-row-menu", html);
        Assert.DoesNotContain("Make admin", html);
        Assert.DoesNotContain("Remove admin", html);
    }

    [Fact]
    public async Task Avatar_IsReplacedAndRemoved_WithTheOtherChanges()
    {
        var user = await CreateUserAsync();
        using var bitmap = new SkiaSharp.SKBitmap(40, 40);
        bitmap.Erase(SkiaSharp.SKColors.Teal);
        using var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var (webp, _, _) = AvatarService.ProcessImage(png.ToArray());

        using (var scope = _factory.Services.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<UserAdministration>();
            var model = (await admin.LoadAsync(user.Id))!;
            model.Avatar = AvatarChange.Replace;
            model.AvatarImage = webp;
            model.Claims.Add(new ClaimEntry { Type = "sub", Value = "spoofed" });   // reserved: the whole edit is refused
            Assert.True((await admin.UpdateAsync(model, null)).Errors.ContainsKey("Claims[0]"));
            Assert.Null((await ReloadAsync(user.Id)).AvatarUrl);

            model.Claims.Clear();
            Assert.True((await admin.UpdateAsync(model, null)).Succeeded);
        }

        Assert.StartsWith($"/avatars/{user.Id}", (await ReloadAsync(user.Id)).AvatarUrl);

        using (var scope = _factory.Services.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<UserAdministration>();
            var model = (await admin.LoadAsync(user.Id))!;
            model.Avatar = AvatarChange.Remove;
            Assert.True((await admin.UpdateAsync(model, null)).Succeeded);
        }

        Assert.Null((await ReloadAsync(user.Id)).AvatarUrl);
    }

    private async Task<User> CreateUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var email = $"edit-{Guid.NewGuid():N}@test.local";
        var user = new User { UserName = email, Email = email, EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        await users.AddToRoleAsync(user, DatabaseInitializer.UserRole);
        return user;
    }

    private async Task<User> ReloadAsync(string id)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AnubisDbContext>().Users.AsNoTracking().SingleAsync(u => u.Id == id);
    }

    private static async Task<bool> SignInAsync(FormClient client, string email)
    {
        (await client.PasswordLoginAsync(email, Password)).Dispose();
        var signedIn = await client.IsSignedInAsync();
        if (signedIn)
        {
            await client.SignOutAsync();
        }

        return signedIn;
    }

    private async Task<List<string>> EventTypesAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AnubisDbContext>().UserEvents
            .Where(e => e.UserId == userId).Select(e => e.Type).ToListAsync();
    }

    /// <summary>The app on its own database, with exactly one administrator (the seeded one).</summary>
    public sealed class Factory : AnubisFactory
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"anubis-user-admin-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = $"Data Source={_dbPath}" }));
        }
    }
}
