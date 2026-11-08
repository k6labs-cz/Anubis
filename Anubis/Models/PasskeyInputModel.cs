namespace k6labs.Anubis.Models;

/// <summary>Form fields posted by the &lt;passkey-submit&gt; element (wwwroot/js/passkey.js).</summary>
public sealed class PasskeyInputModel
{
    /// <summary>The WebAuthn credential returned by the browser, as JSON.</summary>
    public string? CredentialJson { get; set; }

    /// <summary>Set instead of <see cref="CredentialJson"/> when the browser ceremony failed.</summary>
    public string? Error { get; set; }
}
