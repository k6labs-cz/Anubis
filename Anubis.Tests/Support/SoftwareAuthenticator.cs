using System.Buffers.Binary;
using System.Buffers.Text;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Anubis.Tests.Support;

/// <summary>
/// A minimal WebAuthn authenticator in software (ES256, "none" attestation), standing
/// in for a phone's fingerprint sensor. It produces the same JSON a browser posts
/// after navigator.credentials.create()/get().
/// </summary>
public sealed class SoftwareAuthenticator : IDisposable
{
    private const byte UserPresent = 0x01;
    private const byte UserVerified = 0x04;
    private const byte AttestedCredentialData = 0x40;

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _credentialId = RandomNumberGenerator.GetBytes(32);
    private uint _signCount;
    private string? _userHandle;

    public string Origin { get; init; } = "http://localhost";

    /// <summary>Whether the "fingerprint check" passes (the UV flag).</summary>
    public bool VerifyUser { get; set; } = true;

    public string CredentialId => Base64Url.EncodeToString(_credentialId);

    /// <summary>Answers PublicKeyCredentialCreationOptions (JSON) with a new credential.</summary>
    public string Create(string creationOptionsJson)
    {
        var options = JsonNode.Parse(creationOptionsJson)!;
        var rpId = options["rp"]!["id"]!.GetValue<string>();
        _userHandle = options["user"]!["id"]!.GetValue<string>();

        var clientData = ClientData("webauthn.create", options["challenge"]!.GetValue<string>());
        var authData = AuthenticatorData(rpId, includeCredential: true);

        var cbor = new CborWriter();
        cbor.WriteStartMap(3);
        cbor.WriteTextString("fmt");
        cbor.WriteTextString("none");
        cbor.WriteTextString("attStmt");
        cbor.WriteStartMap(0);
        cbor.WriteEndMap();
        cbor.WriteTextString("authData");
        cbor.WriteByteString(authData);
        cbor.WriteEndMap();

        return JsonSerializer.Serialize(new
        {
            id = CredentialId,
            rawId = CredentialId,
            type = "public-key",
            authenticatorAttachment = "platform",
            clientExtensionResults = new { },
            response = new
            {
                clientDataJSON = Base64Url.EncodeToString(clientData),
                attestationObject = Base64Url.EncodeToString(cbor.Encode()),
                transports = new[] { "internal" },
            },
        });
    }

    /// <summary>Answers PublicKeyCredentialRequestOptions (JSON) with a signed assertion.</summary>
    public string Get(string requestOptionsJson, Func<byte[], byte[]>? tamperSignature = null)
    {
        var options = JsonNode.Parse(requestOptionsJson)!;
        var rpId = options["rpId"]!.GetValue<string>();

        var clientData = ClientData("webauthn.get", options["challenge"]!.GetValue<string>());
        var authData = AuthenticatorData(rpId, includeCredential: false);
        var signature = _key.SignData([.. authData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        signature = tamperSignature?.Invoke(signature) ?? signature;

        return JsonSerializer.Serialize(new
        {
            id = CredentialId,
            rawId = CredentialId,
            type = "public-key",
            authenticatorAttachment = "platform",
            clientExtensionResults = new { },
            response = new
            {
                clientDataJSON = Base64Url.EncodeToString(clientData),
                authenticatorData = Base64Url.EncodeToString(authData),
                signature = Base64Url.EncodeToString(signature),
                userHandle = _userHandle,
            },
        });
    }

    private byte[] ClientData(string type, string challenge)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, challenge, origin = Origin, crossOrigin = false }));

    private byte[] AuthenticatorData(string rpId, bool includeCredential)
    {
        var data = new List<byte>(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)));
        var flags = (byte)(UserPresent | (VerifyUser ? UserVerified : 0) | (includeCredential ? AttestedCredentialData : 0));
        data.Add(flags);

        var counter = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(counter, ++_signCount);
        data.AddRange(counter);

        if (includeCredential)
        {
            data.AddRange(new byte[16]); // AAGUID: unknown authenticator model
            var length = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)_credentialId.Length);
            data.AddRange(length);
            data.AddRange(_credentialId);
            data.AddRange(CosePublicKey());
        }

        return [.. data];
    }

    private byte[] CosePublicKey()
    {
        var parameters = _key.ExportParameters(includePrivateParameters: false);
        var cbor = new CborWriter();
        cbor.WriteStartMap(5);
        cbor.WriteInt32(1);  // kty
        cbor.WriteInt32(2);  //   EC2
        cbor.WriteInt32(3);  // alg
        cbor.WriteInt32(-7); //   ES256
        cbor.WriteInt32(-1); // crv
        cbor.WriteInt32(1);  //   P-256
        cbor.WriteInt32(-2); // x
        cbor.WriteByteString(parameters.Q.X!);
        cbor.WriteInt32(-3); // y
        cbor.WriteByteString(parameters.Q.Y!);
        cbor.WriteEndMap();
        return cbor.Encode();
    }

    public void Dispose() => _key.Dispose();
}
