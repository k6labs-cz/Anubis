using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace k6labs.Anubis.Hosting;

/// <summary>
/// Signing and encryption certificates of the OpenID Connect server. Either configured
/// (<c>Anubis:Keys:SigningCertificatePath</c> / <c>...Password</c> and the same for
/// <c>Encryption</c>, e.g. from a Kubernetes Secret) or generated once into the data
/// directory (<c>keys/signing-*.pfx</c>, <c>keys/encryption-*.pfx</c>) and reused on every start,
/// so issued tokens stay valid across restarts and pods.
/// Generated certificates are valid for two years; 60 days before expiry a new one is created
/// and becomes the active key, while older ones that have not expired yet are still loaded so
/// existing tokens can be validated.
/// </summary>
public static class OpenIddictCertificates
{
    public const string SectionName = "Anubis:Keys";

    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(730);
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(60);

    public enum Purpose
    {
        Signing,
        Encryption,
    }

    /// <summary>Certificates for a purpose, newest first. Generates one when none is usable.</summary>
    public static IReadOnlyList<X509Certificate2> Load(IConfiguration configuration, DataPaths paths, Purpose purpose, DateTimeOffset? now = null)
    {
        var name = purpose.ToString();
        var section = configuration.GetSection(SectionName);
        if (section[$"{name}CertificatePath"] is { Length: > 0 } configuredPath)
        {
            return [LoadPfx(paths.Resolve(configuredPath), section[$"{name}CertificatePassword"])];
        }

        var current = now ?? DateTimeOffset.UtcNow;
        var prefix = name.ToLowerInvariant();
        var certificates = Directory.EnumerateFiles(paths.KeysDirectory, $"{prefix}-*.pfx")
            .Select(path => LoadPfx(path, password: null))
            .Where(certificate => certificate.NotAfter.ToUniversalTime() > current.UtcDateTime)
            .OrderByDescending(certificate => certificate.NotAfter)
            .ToList();

        if (certificates.Count == 0 || certificates[0].NotAfter.ToUniversalTime() - current.UtcDateTime < RenewBefore)
        {
            certificates.Insert(0, Create(paths.KeysDirectory, purpose, current));
        }

        return certificates;
    }

    private static X509Certificate2 Create(string directory, Purpose purpose, DateTimeOffset now)
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest($"CN=Anubis {purpose} Certificate", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            purpose == Purpose.Signing ? X509KeyUsageFlags.DigitalSignature : X509KeyUsageFlags.KeyEncipherment, critical: true));

        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.Add(Lifetime));
        var path = Path.Combine(directory, $"{purpose.ToString().ToLowerInvariant()}-{now:yyyyMMddHHmmss}.pfx");
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, certificate.Export(X509ContentType.Pkcs12));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // the private key: owner only
        }

        File.Move(temp, path, overwrite: true);
        return LoadPfx(path, password: null);
    }

    private static X509Certificate2 LoadPfx(string path, string? password)
        => X509CertificateLoader.LoadPkcs12FromFile(path, password,
            // Keep the private key in memory only (macOS does not support ephemeral key sets).
            OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
}
