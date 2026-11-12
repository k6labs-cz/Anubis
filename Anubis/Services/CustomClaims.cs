using System.Security.Claims;
using System.Text.RegularExpressions;

using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Models;

using Microsoft.AspNetCore.Identity;

using Group = k6labs.Anubis.Data.Entities.Group;

namespace k6labs.Anubis.Services;

/// <summary>
/// Claims an administrator sets on a group or directly on a user. A user gets the claims of all
/// their groups plus their own; they are added to the access token, the ID token and userinfo.
/// Claim types that Anubis or OpenID Connect set themselves (sub, email, role, groups, exp, ...) are refused,
/// so a custom claim can never replace or fake one of them.
/// </summary>
public static partial class CustomClaims
{
    public const int TypeMaxLength = 256;
    public const int ValueMaxLength = 1024;
    public const int MaxCount = 100;

    /// <summary>Error keys: "Claims" for the whole list, "Claims[i]" for one row.</summary>
    public const string Field = "Claims";

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        // OpenID Connect / JWT
        "sub", "iss", "aud", "exp", "iat", "nbf", "jti", "auth_time", "nonce", "acr", "amr", "azp", "at_hash", "c_hash", "sid",
        "client_id", "scope", "scp", "cnf", "typ", "htm", "htu", "act", "may_act", "events", "sub_jwk",
        // Set by Anubis
        "name", "preferred_username", "picture", "email", "email_verified", "phone_number", "phone_number_verified", "role", "roles", "groups", "tenant",
        "given_name", "family_name", "middle_name", "nickname", "profile", "website", "gender", "birthdate", "zoneinfo", "locale", "address", "updated_at",
        // ASP.NET Core Identity (sign-in cookie)
        "AspNet.Identity.SecurityStamp", "amr",
    };

    private static readonly string[] ReservedPrefixes =
    [
        "oi_",                                   // OpenIddict internals
        "http://schemas.microsoft.com/",          // ClaimTypes.* (role, name identifier, ...)
        "http://schemas.xmlsoap.org/",
        "AspNet.",
    ];

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_\-\.:/]*$")]
    private static partial Regex TypePattern();

    public static bool IsReserved(string type)
        => Reserved.Contains(type) || ReservedPrefixes.Any(prefix => type.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Trims the rows, drops completely empty ones and validates the rest.</summary>
    public static List<Claim> Validate(IEnumerable<ClaimEntry> entries, IDictionary<string, string> errors)
    {
        var claims = new List<Claim>();
        var seen = new HashSet<(string, string)>();
        var index = -1;
        foreach (var entry in entries)
        {
            index++;
            var type = entry.Type?.Trim() ?? string.Empty;
            var value = entry.Value?.Trim() ?? string.Empty;
            if (type.Length == 0 && value.Length == 0)
            {
                continue;
            }

            var field = $"{Field}[{index}]";
            string? error = null;
            if (type.Length == 0 || type.Length > TypeMaxLength || !TypePattern().IsMatch(type))
            {
                error = Translations.Validation_ClaimTypeInvalid.F(TypeMaxLength);
            }
            else if (IsReserved(type))
            {
                error = Translations.Validation_ClaimTypeReserved.F(type);
            }
            else if (value.Length == 0)
            {
                error = Translations.Validation_ClaimValueRequired;
            }
            else if (value.Length > ValueMaxLength)
            {
                error = Translations.Validation_ClaimValueTooLong.F(ValueMaxLength);
            }
            else if (!seen.Add((type, value)))
            {
                error = Translations.Validation_ClaimDuplicate;
            }

            if (error is not null)
            {
                errors.TryAdd(field, error);
                continue;
            }

            claims.Add(new Claim(type, value));
        }

        if (claims.Count > MaxCount)
        {
            errors.TryAdd(Field, Translations.Validation_ClaimTooMany.F(MaxCount));
        }

        return claims;
    }

    public static List<ClaimEntry> ToEntries(IEnumerable<Claim> claims)
        => claims.Where(claim => !IsReserved(claim.Type))
                 .OrderBy(claim => claim.Type, StringComparer.Ordinal).ThenBy(claim => claim.Value, StringComparer.Ordinal)
                 .Select(claim => new ClaimEntry { Type = claim.Type, Value = claim.Value })
                 .ToList();

    /// <summary>Claims of the given groups (group name, claim), for showing what a user inherits.</summary>
    public static async Task<List<(string Group, Claim Claim)>> OfGroupsAsync(RoleManager<Group> groups, IEnumerable<string> groupNames)
    {
        var result = new List<(string, Claim)>();
        foreach (var name in groupNames.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (await groups.FindByNameAsync(name) is { } group)
            {
                result.AddRange((await groups.GetClaimsAsync(group)).Where(claim => !IsReserved(claim.Type)).Select(claim => (name, claim)));
            }
        }

        return result;
    }

    /// <summary>All custom claims of a user (from groups and their own), without duplicates, grouped by type.</summary>
    public static async Task<IReadOnlyDictionary<string, List<string>>> ForUserAsync(UserManager<User> users, RoleManager<Group> groups, User user)
    {
        var all = (await OfGroupsAsync(groups, await users.GetRolesAsync(user))).Select(item => item.Claim)
            .Concat((await users.GetClaimsAsync(user)).Where(claim => !IsReserved(claim.Type)));

        var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var claim in all)
        {
            var values = grouped.TryGetValue(claim.Type, out var list) ? list : grouped[claim.Type] = new List<string>();
            if (!values.Contains(claim.Value, StringComparer.Ordinal))
            {
                values.Add(claim.Value);
            }
        }

        return grouped;
    }
}
