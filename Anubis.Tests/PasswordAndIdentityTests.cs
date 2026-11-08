using k6labs.Anubis.Data;
using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Identity;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Anubis.Tests;

[Collection(AnubisCollection.Name)]
public class PasswordAndIdentityTests
{
    private readonly AnubisFactory _factory;

    public PasswordAndIdentityTests(AnubisFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SeededAdmin_CanSignInWithConfiguredPassword()
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var signInManager = scope.ServiceProvider.GetRequiredService<SignInManager<User>>();

        var admin = await userManager.FindByEmailAsync(AnubisFactory.AdminEmail);

        Assert.NotNull(admin);
        Assert.True(await userManager.IsInRoleAsync(admin, DatabaseInitializer.AdminRole));

        var result = await signInManager.CheckPasswordSignInAsync(admin, AnubisFactory.AdminPassword, lockoutOnFailure: false);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task SeededAdmin_WrongPassword_Fails()
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var signInManager = scope.ServiceProvider.GetRequiredService<SignInManager<User>>();

        var admin = await userManager.FindByEmailAsync(AnubisFactory.AdminEmail);
        Assert.NotNull(admin);

        var result = await signInManager.CheckPasswordSignInAsync(admin, "definitely-wrong-password", lockoutOnFailure: false);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Argon2idHasher_ProducesPhcHash_AndVerifies()
    {
        var hasher = new Argon2idPasswordHasher<User>();
        var user = new User { UserName = "u@example.com", Email = "u@example.com" };

        var hash = hasher.HashPassword(user, "correct horse battery staple");

        Assert.StartsWith("$argon2id$", hash);
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(user, hash, "correct horse battery staple"));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(user, hash, "wrong password"));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(user, "not-a-phc-hash", "wrong password"));
    }

    [Fact]
    public async Task FirstCreatedUser_WhenDatabaseHasNoUsers_BecomesAdminAutomatically()
    {
        await using var factory = new FirstUserAdminFactory();
        try
        {
            using var client = new Support.FormClient(factory);
            var firstEmail = $"first-{Guid.NewGuid():N}@test.local";
            var secondEmail = $"second-{Guid.NewGuid():N}@test.local";

            (await AccountEmailTests.RegisterAsync(client, firstEmail)).Dispose();
            await client.SignOutAsync();
            (await AccountEmailTests.RegisterAsync(client, secondEmail)).Dispose();

            using var scope = factory.Services.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

            var firstUser = await userManager.FindByEmailAsync(firstEmail);
            var secondUser = await userManager.FindByEmailAsync(secondEmail);

            Assert.NotNull(firstUser);
            Assert.NotNull(secondUser);
            Assert.True(await userManager.IsInRoleAsync(firstUser, DatabaseInitializer.AdminRole));
            Assert.True(await userManager.IsInRoleAsync(firstUser, DatabaseInitializer.UserRole));
            Assert.False(await userManager.IsInRoleAsync(secondUser, DatabaseInitializer.AdminRole));
            Assert.True(await userManager.IsInRoleAsync(secondUser, DatabaseInitializer.UserRole));
        }
        finally
        {
            factory.DeleteDatabaseFile();
        }
    }

    private sealed class FirstUserAdminFactory : AnubisFactory
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"anubis-first-admin-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = $"Data Source={_dbPath}",
                    ["Anubis:AdminPassword"] = string.Empty,
                });
            });
        }

        public void DeleteDatabaseFile()
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
    }
}
