using k6labs.Anubis.Controllers.Scim;

namespace k6labs.Anubis.Scim;

/// <summary>Schema URNs, media type and other constants of the SCIM 2.0 API (RFC 7643 / 7644) under /api/.</summary>
public static class ScimConstants
{
    /// <summary>Media type of every SCIM request and response (in addition to plain application/json).</summary>
    public const string MediaType = "application/scim+json";

    public const string UserSchema = "urn:ietf:params:scim:schemas:core:2.0:User";
    public const string GroupSchema = "urn:ietf:params:scim:schemas:core:2.0:Group";
    public const string ListResponseSchema = "urn:ietf:params:scim:api:messages:2.0:ListResponse";
    public const string PatchRequestSchema = "urn:ietf:params:scim:api:messages:2.0:PatchRequest";
    public const string ErrorSchema = "urn:ietf:params:scim:api:messages:2.0:Error";
    public const string ServiceProviderConfigSchema = "urn:ietf:params:scim:api:messages:2.0:ServiceProviderConfig";
    public const string ResourceTypeSchema = "urn:ietf:params:scim:schemas:core:2.0:ResourceType";
    public const string SchemaSchema = "urn:ietf:params:scim:schemas:core:2.0:Schema";

    /// <summary>Extension schema carrying the Anubis-specific user attributes (custom claims, email confirmation).</summary>
    public const string UserExtensionSchema = "urn:ietf:params:scim:schemas:extension:anubis:2.0:User";

    /// <summary>Error scimTypes (RFC 7644 section 3.12).</summary>
    public static class ScimTypes
    {
        public const string InvalidSyntax = "invalidSyntax";
        public const string InvalidFilter = "invalidFilter";
        public const string InvalidPath = "invalidPath";
        public const string InvalidValue = "invalidValue";
        public const string Uniqueness = "uniqueness";
        public const string Mutability = "mutability";
        public const string NoTarget = "noTarget";
    }

    /// <summary>Default and maximum page size of list responses (the "count" query parameter).</summary>
    public const int DefaultCount = 50;
    public const int MaxCount = 200;
}

/// <summary>A SCIM error (RFC 7644 section 3.12), thrown by the service layer and rendered by <see cref="ScimExceptionFilter"/>.</summary>
public sealed class ScimException : Exception
{
    public int Status { get; }

    /// <summary>One of <see cref="ScimConstants.ScimTypes"/>; null for plain status errors.</summary>
    public string? ScimType { get; }

    public ScimException(int status, string? scimType, string detail)
        : base(detail)
    {
        Status = status;
        ScimType = scimType;
    }

    public static ScimException NotFound(string resourceType, string id)
        => new(StatusCodes.Status404NotFound, null, $"Resource {id} of type {resourceType} was not found.");

    public static ScimException InvalidFilter(string detail)
        => new(StatusCodes.Status400BadRequest, ScimConstants.ScimTypes.InvalidFilter, detail);

    public static ScimException InvalidPath(string path)
        => new(StatusCodes.Status400BadRequest, ScimConstants.ScimTypes.InvalidPath, $"The path \"{path}\" is not supported.");

    public static ScimException InvalidSyntax(string detail)
        => new(StatusCodes.Status400BadRequest, ScimConstants.ScimTypes.InvalidSyntax, detail);

    public static ScimException InvalidValue(string detail)
        => new(StatusCodes.Status400BadRequest, ScimConstants.ScimTypes.InvalidValue, detail);
}
