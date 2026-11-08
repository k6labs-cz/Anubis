namespace k6labs.Anubis.Options;

/// <summary>
/// General settings, bound to <c>Anubis:General</c> (edited on the admin "General" page,
/// saved to the settings file).
/// </summary>
public sealed class GeneralOptions
{
    public const string SectionName = "Anubis:General";

    public const string DefaultServerName = "Anubis";

    public const int ServerNameMaxLength = 60;

    /// <summary>Name of this server: suffix of every browser tab title, and shown top left on the account and admin pages.</summary>
    public string ServerName { get; set; } = DefaultServerName;

    /// <summary><see cref="ServerName"/>, or the default when it is empty.</summary>
    public string EffectiveServerName => string.IsNullOrWhiteSpace(ServerName) ? DefaultServerName : ServerName.Trim();
}
