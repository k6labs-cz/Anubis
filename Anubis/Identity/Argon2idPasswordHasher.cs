using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Isopoh.Cryptography.Argon2;

using k6labs.Anubis.Telemetry;

using Microsoft.AspNetCore.Identity;

namespace k6labs.Anubis.Identity;

/// <summary>
/// ASP.NET Core Identity password hasher backed by Argon2id.
/// Hashes are stored in PHC string format (<c>$argon2id$v=19$m=...,t=...,p=...$salt$hash</c>)
/// with a unique random salt per password.
/// </summary>
public sealed class Argon2idPasswordHasher<TUser> : IPasswordHasher<TUser> where TUser : class
{
    // OWASP-recommended Argon2id parameters: 64 MiB memory, 3 iterations, parallelism 4.
    private const int MemoryCostKiB = 64 * 1024;
    private const int TimeCost = 3;
    private const int Lanes = 4;
    private const int HashLength = 32;
    private const int SaltLength = 16;

    public string HashPassword(TUser user, string password)
    {
        using var activity = AnubisTelemetry.ActivitySource.StartActivity("password.hash");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var config = new Argon2Config
        {
            Type = Argon2Type.HybridAddressing, // Argon2id
            Version = Argon2Version.Nineteen,
            TimeCost = TimeCost,
            MemoryCost = MemoryCostKiB,
            Lanes = Lanes,
            Threads = Lanes,
            HashLength = HashLength,
            Password = Encoding.UTF8.GetBytes(password),
            Salt = RandomNumberGenerator.GetBytes(SaltLength),
        };

        var hash = Argon2.Hash(config);
        AnubisTelemetry.PasswordHashDuration.Record(
            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds, new KeyValuePair<string, object?>("anubis.password.operation", "hash"));
        return hash;
    }

    public PasswordVerificationResult VerifyHashedPassword(TUser user, string hashedPassword, string providedPassword)
    {
        if (string.IsNullOrEmpty(hashedPassword))
        {
            return PasswordVerificationResult.Failed;
        }

        bool valid;
        using var activity = AnubisTelemetry.ActivitySource.StartActivity("password.verify");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            valid = Argon2.Verify(hashedPassword, providedPassword);
            AnubisTelemetry.PasswordHashDuration.Record(
                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds, new KeyValuePair<string, object?>("anubis.password.operation", "verify"));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            return PasswordVerificationResult.Failed;
        }

        if (!valid)
        {
            return PasswordVerificationResult.Failed;
        }

        return NeedsRehash(hashedPassword)
            ? PasswordVerificationResult.SuccessRehashNeeded
            : PasswordVerificationResult.Success;
    }

    /// <summary>Returns true when the PHC-encoded hash uses weaker parameters than the current defaults.</summary>
    private static bool NeedsRehash(string phc)
    {
        try
        {
            // PHC format: $argon2id$v=19$m=65536,t=3,p=4$<salt>$<hash>
            var parts = phc.Split('$');
            if (parts.Length < 5 || !parts[1].Equals("argon2id", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var parameters = parts[3].Split(',');
            var memory = int.Parse(parameters[0].Substring(2), CultureInfo.InvariantCulture); // "m=65536"
            var time = int.Parse(parameters[1].Substring(2), CultureInfo.InvariantCulture);   // "t=3"
            return memory < MemoryCostKiB || time < TimeCost;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException)
        {
            return true;
        }
    }
}
