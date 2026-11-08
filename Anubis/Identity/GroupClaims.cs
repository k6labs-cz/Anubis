namespace k6labs.Anubis.Identity;

/// <summary>
/// The user's groups in tokens: always in the access token, in the ID token and userinfo when
/// the client asks for the "groups" scope. The claim is named "groups" as in Keycloak and Entra ID.
/// </summary>
public static class GroupClaims
{
    public const string Scope = "groups";

    public const string Claim = "groups";
}
