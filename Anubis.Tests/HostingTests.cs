using k6labs.Anubis.Hosting;

using Microsoft.Extensions.Configuration;

namespace Anubis.Tests;

/// <summary>
/// Container / Kubernetes readiness: one data directory for the database, settings and keys,
/// persistent OpenIddict certificates, and configuration mounted from ConfigMaps / Secrets.
/// </summary>
public class HostingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"anubis-hosting-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private DataPaths Paths(string? dataDirectory = null) => DataPaths.Resolve(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [DataPaths.DataDirectoryKey] = dataDirectory ?? Path.Combine(_root, "data") }).Build(),
        _root);

    [Fact]
    public void DataDirectory_HoldsTheDatabase_AndCreatesTheKeysFolder()
    {
        var paths = Paths();

        Assert.Equal(Path.Combine(_root, "data"), paths.DataDirectory);
        Assert.True(Directory.Exists(paths.KeysDirectory));
        Assert.Equal($"Data Source={Path.Combine(_root, "data", "anubis.db")}", paths.SqliteConnectionString(null));
        Assert.Equal($"Data Source={Path.Combine(_root, "data", "other.db")}", paths.SqliteConnectionString("Data Source=other.db"));
        Assert.Equal("Data Source=/mnt/db/anubis.db", paths.SqliteConnectionString("Data Source=/mnt/db/anubis.db"));   // absolute stays
        Assert.Equal("Data Source=:memory:", paths.SqliteConnectionString("Data Source=:memory:"));
    }

    [Fact]
    public void EveryTenant_HasItsOwnDatabaseFile_AndTheCatalogListsThem()
    {
        var paths = Paths();
        var acme = new k6labs.Anubis.Tenancy.Tenant { Id = "acme" };

        Assert.Equal(paths.SqliteConnectionString(null), paths.TenantConnectionString(k6labs.Anubis.Tenancy.Tenant.Default, null));
        Assert.Equal($"Data Source={Path.Combine(_root, "data", "tenants", "acme", "anubis.db")}", paths.TenantConnectionString(acme, null));
        // The other settings of the connection string are the tenant's too; only the file differs.
        Assert.Equal($"Data Source={Path.Combine(_root, "data", "tenants", "acme", "anubis.db")};Foreign Keys=True",
            paths.TenantConnectionString(acme, "Data Source=/mnt/db/main.db;Foreign Keys=True"));
        Assert.Equal($"Data Source={Path.Combine(_root, "data", "anubis.tenants.db")}", paths.CatalogConnectionString(null));
        Assert.Equal("Data Source=/mnt/db/catalog.db", paths.CatalogConnectionString("Data Source=/mnt/db/catalog.db"));
    }

    [Fact]
    public void DataDirectory_DefaultsToTheContentRoot()
    {
        var paths = DataPaths.Resolve(new ConfigurationBuilder().Build(), _root);

        Assert.Equal(_root, paths.DataDirectory);
    }

    [Fact]
    public void Certificates_AreGeneratedOnce_ThenReused_AndRenewedBeforeExpiry()
    {
        var paths = Paths();
        var configuration = new ConfigurationBuilder().Build();

        var first = OpenIddictCertificates.Load(configuration, paths, OpenIddictCertificates.Purpose.Signing);
        var again = OpenIddictCertificates.Load(configuration, paths, OpenIddictCertificates.Purpose.Signing);
        Assert.Single(first);
        Assert.True(first[0].HasPrivateKey);
        Assert.Equal(first[0].Thumbprint, Assert.Single(again).Thumbprint);
        Assert.Single(Directory.GetFiles(paths.KeysDirectory, "signing-*.pfx"));

        // 700 days later (60 days before expiry): a new active certificate, the old one kept for validation.
        var later = OpenIddictCertificates.Load(configuration, paths, OpenIddictCertificates.Purpose.Signing, DateTimeOffset.UtcNow.AddDays(700));
        Assert.Equal(2, later.Count);
        Assert.NotEqual(first[0].Thumbprint, later[0].Thumbprint);
        Assert.Equal(first[0].Thumbprint, later[1].Thumbprint);

        var encryption = OpenIddictCertificates.Load(configuration, paths, OpenIddictCertificates.Purpose.Encryption);
        Assert.Single(encryption);
        Assert.NotEqual(first[0].Thumbprint, encryption[0].Thumbprint);

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(Directory.GetFiles(paths.KeysDirectory, "encryption-*.pfx")[0]);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);   // private key readable by the owner only
        }
    }

    [Fact]
    public void ConfiguredCertificate_IsUsedInsteadOfGeneratedOnes()
    {
        var paths = Paths();
        var generated = OpenIddictCertificates.Load(new ConfigurationBuilder().Build(), paths, OpenIddictCertificates.Purpose.Signing)[0];
        var file = Path.Combine(_root, "provided.pfx");
        File.Copy(Directory.GetFiles(paths.KeysDirectory, "signing-*.pfx")[0], file);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Anubis:Keys:SigningCertificatePath"] = file,
        }).Build();

        Assert.Equal(generated.Thumbprint, Assert.Single(OpenIddictCertificates.Load(configuration, Paths(Path.Combine(_root, "empty")), OpenIddictCertificates.Purpose.Signing)).Thumbprint);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "empty", "keys")));   // nothing generated
    }

    [Fact]
    public void MountedConfiguration_SitsBelowEnvironmentVariables()
    {
        Directory.CreateDirectory(Path.Combine(_root, "secrets"));
        File.WriteAllText(Path.Combine(_root, "config.json"), """{ "Email": { "FromName": "From ConfigMap", "FromAddress": "configmap@example.com" }, "Anubis": { "AllowRegistration": false } }""");
        File.WriteAllText(Path.Combine(_root, "secrets", "Email__FromName"), "From Secret");
        var variable = $"ANUBIS_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable($"{variable}__FromAddress", "env@example.com");
        try
        {
            var builder = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Email:FromName"] = "From appsettings",
                    [ConfigurationSources.ConfigFileKey] = "config.json",
                    [ConfigurationSources.SecretsDirectoryKey] = "secrets",
                })
                .AddEnvironmentVariables();

            ConfigurationSources.AddMountedSources(builder, _root);
            var configuration = builder.Build();

            Assert.Equal("From Secret", configuration["Email:FromName"]);              // Secret > ConfigMap > appsettings
            Assert.Equal("False", configuration["Anubis:AllowRegistration"]);           // from the ConfigMap
            Assert.Equal("env@example.com", configuration[$"{variable}:FromAddress"]); // environment variables still on top
            Assert.Equal("configmap@example.com", configuration["Email:FromAddress"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable($"{variable}__FromAddress", null);
        }
    }
}
