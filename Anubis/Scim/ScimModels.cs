using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace k6labs.Anubis.Scim;

/// <summary>SCIM name complex attribute (RFC 7643 section 4.1.2). Only "formatted" is stored (as the display name).</summary>
public sealed class ScimName
{
    [JsonPropertyName("givenName")] public string? GivenName { get; set; }
    [JsonPropertyName("familyName")] public string? FamilyName { get; set; }
    [JsonPropertyName("formatted")] public string? Formatted { get; set; }
}

public sealed class ScimEmail
{
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("primary")] public bool? Primary { get; set; }
    [JsonPropertyName("display")] public string? Display { get; set; }
}

public sealed class ScimPhone
{
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("primary")] public bool? Primary { get; set; }
}

/// <summary>Group reference on a user (RFC 7643 section 4.1.2 "groups").</summary>
public sealed class ScimGroupRef
{
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("display")] public string? Display { get; set; }
    [JsonPropertyName("$ref")] public string? Ref { get; set; }
}

/// <summary>Member reference on a group (RFC 7643 section 4.2.2 "members").</summary>
public sealed class ScimMember
{
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("display")] public string? Display { get; set; }
    [JsonPropertyName("$ref")] public string? Ref { get; set; }
}

/// <summary>Custom claim in the Anubis user extension (type/value pair, like <see cref="Anubis.Models.ClaimEntry"/>).</summary>
public sealed class ScimClaim
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
}

/// <summary>The Anubis extension of the user schema (<see cref="ScimConstants.UserExtensionSchema"/>).</summary>
public sealed class ScimUserExtension
{
    [JsonPropertyName("claims")] public List<ScimClaim>? Claims { get; set; }
    [JsonPropertyName("emailConfirmed")] public bool? EmailConfirmed { get; set; }
}

/// <summary>SCIM user resource as sent by clients on POST / PUT (responses are built as <see cref="JsonObject"/>).</summary>
public sealed class ScimUserResource
{
    [JsonPropertyName("schemas")] public List<string>? Schemas { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("userName")] public string? UserName { get; set; }
    [JsonPropertyName("name")] public ScimName? Name { get; set; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("active")] public bool? Active { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("emails")] public List<ScimEmail>? Emails { get; set; }
    [JsonPropertyName("phoneNumbers")] public List<ScimPhone>? PhoneNumbers { get; set; }
    [JsonPropertyName("groups")] public List<ScimGroupRef>? Groups { get; set; }
    [JsonPropertyName(ScimConstants.UserExtensionSchema)] public ScimUserExtension? Extension { get; set; }
}

/// <summary>SCIM group resource as sent by clients on POST / PUT.</summary>
public sealed class ScimGroupResource
{
    [JsonPropertyName("schemas")] public List<string>? Schemas { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("members")] public List<ScimMember>? Members { get; set; }
}

/// <summary>One operation of a PATCH request (RFC 7644 section 3.5.2).</summary>
public sealed class ScimPatchOperation
{
    [JsonPropertyName("op")] public string? Op { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("value")] public JsonNode? Value { get; set; }
}

/// <summary>PATCH request body (RFC 7644 section 3.5.2).</summary>
public sealed class ScimPatchRequest
{
    [JsonPropertyName("schemas")] public List<string>? Schemas { get; set; }
    [JsonPropertyName("Operations")] public List<ScimPatchOperation>? Operations { get; set; }
}

/// <summary>Parsed list filter: attribute path, comparison operator and the compared string value.</summary>
public sealed record ScimFilterTerm(string Path, string Op, string Value);

/// <summary>
/// Parser of the SCIM "filter" query parameter (RFC 7644 section 3.4.2.2). Supported: single attribute
/// comparisons (eq, ne, co, sw, ew) joined by "and"; attribute paths like "emails[type eq \"work\"].value".
/// String comparison is case-insensitive, as SCIM prescribes for these operators.
/// </summary>
public static partial class ScimFilter
{
    [GeneratedRegex(@"^\s*(?<path>[A-Za-z0-9_\-:\.]+(?:\[(?<filter>[^\]]+)\])?(?:\.(?<sub>[A-Za-z0-9_\-]+))?)\s+(?<op>eq|ne|co|sw|ew)\s+""(?<value>(?:[^""\\]|\\.)*)""\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TermRegex();

    [GeneratedRegex(@"\s+and\s+", RegexOptions.IgnoreCase)]
    private static partial Regex AndRegex();

    /// <summary>Parses the filter; throws <see cref="ScimException"/> (invalidFilter) when it cannot be parsed.</summary>
    public static List<ScimFilterTerm> Parse(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return new List<ScimFilterTerm>();
        }

        var terms = new List<ScimFilterTerm>();
        foreach (var part in AndRegex().Split(filter))
        {
            var match = TermRegex().Match(part);
            if (!match.Success)
            {
                throw ScimException.InvalidFilter($"Could not parse the filter \"{filter}\".");
            }

            var path = match.Groups["path"].Value;
            if (match.Groups["filter"].Success)
            {
                path += "[" + match.Groups["filter"].Value.Trim() + "]";
            }

            if (match.Groups["sub"].Success)
            {
                path += "." + match.Groups["sub"].Value;
            }

            var value = match.Groups["value"].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
            terms.Add(new ScimFilterTerm(path.ToLowerInvariant(), match.Groups["op"].Value.ToLowerInvariant(), value));
        }

        return terms;
    }

    /// <summary>Extracts the string value of a resource attribute for a filter term, or null when the path does not match.</summary>
    public static string? AttributeValue(JsonObject resource, string path)
    {
        // Normalize "emails[type eq \"work\"].value" -> look at emails array, pick the entry with type == work, take .value.
        var segments = path.Split('.');
        JsonNode? node = resource;
        foreach (var segment in segments)
        {
            var bracket = segment.IndexOf('[');
            var name = (bracket < 0 ? segment : segment[..bracket]).ToLowerInvariant();
            if (node is not JsonObject obj)
            {
                return null;
            }

            // SCIM attribute names are case-insensitive ("userName" == "username").
            node = obj.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            if (node is null)
            {
                return null;
            }

            if (bracket >= 0 && node is JsonArray array)
            {
                var inner = segment[(bracket + 1)..].TrimEnd(']');
                // Parse "attr op \"value\"" inside the brackets.
                var m = Regex.Match(inner, @"^\s*(?<attr>[A-Za-z0-9_\-]+)\s+(?<op>eq|ne)\s+""(?<val>(?:[^""\\]|\\.)*)""\s*$", RegexOptions.IgnoreCase);
                if (!m.Success)
                {
                    return null;
                }

                var attr = m.Groups["attr"].Value.ToLowerInvariant();
                var op = m.Groups["op"].Value.ToLowerInvariant();
                var val = m.Groups["val"].Value;
                node = array.FirstOrDefault(item =>
                    item is JsonObject entry
                    && entry.FirstOrDefault(p => string.Equals(p.Key, attr, StringComparison.OrdinalIgnoreCase)).Value is JsonValue jv
                    && jv.TryGetValue<string>(out var s)
                    && (op == "eq" ? string.Equals(s, val, StringComparison.OrdinalIgnoreCase) : !string.Equals(s, val, StringComparison.OrdinalIgnoreCase)));
                if (node is null)
                {
                    return null;
                }
            }
        }

        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text
            : node is JsonValue b && b.TryGetValue<bool>(out var boolean) ? boolean.ToString().ToLowerInvariant()
            : null;
    }

    /// <summary>True when the resource matches every term.</summary>
    public static bool Matches(JsonObject resource, IEnumerable<ScimFilterTerm> terms)
    {
        foreach (var term in terms)
        {
            var actual = AttributeValue(resource, term.Path);
            var ok = term.Op switch
            {
                "eq" => string.Equals(actual, term.Value, StringComparison.OrdinalIgnoreCase),
                "ne" => !string.Equals(actual, term.Value, StringComparison.OrdinalIgnoreCase),
                "co" => actual?.Contains(term.Value, StringComparison.OrdinalIgnoreCase) == true,
                "sw" => actual?.StartsWith(term.Value, StringComparison.OrdinalIgnoreCase) == true,
                "ew" => actual?.EndsWith(term.Value, StringComparison.OrdinalIgnoreCase) == true,
                _ => false,
            };
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
