using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.X509;

namespace XAOCEN.ReWiFi;

internal sealed class OfflineLicenseService
{
    private const string PrimaryKeyId = "primary";
    private const string PrimaryPublicKeyPem = "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEA7QaPUj1ZkJya2P2p072FhsNIa9iDHq7E4SRk3mDemCo=\n-----END PUBLIC KEY-----\n";
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private readonly WindowsCredentialStore _credentialStore;

    public OfflineLicenseService(WindowsCredentialStore? credentialStore = null)
    {
        _credentialStore = credentialStore ?? new WindowsCredentialStore();
    }

    public string GetOrCreateDevicePublicKey()
    {
        var privateKey = GetOrCreatePrivateKey();
        var publicKey = privateKey.GeneratePublicKey();
        var subjectPublicKeyInfo = SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(publicKey);
        return PemEncoding.WriteString("PUBLIC KEY", subjectPublicKeyInfo.GetDerEncoded());
    }

    public string GetDeviceKeyHash()
    {
        var publicKeyPem = GetOrCreateDevicePublicKey();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(publicKeyPem))).ToLowerInvariant();
    }

    public OfflineLicenseValidationResult ValidateCompactLicense(
        string compactLicense,
        string? expectedDeviceKeyHash = null,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(compactLicense))
        {
            return OfflineLicenseValidationResult.Fail(OfflineLicenseValidationStatus.Malformed, "离线授权为空。");
        }

        var parts = compactLicense.Split('.');
        if (parts.Length != 2 || !IsBase64Url(parts[0]) || !IsBase64Url(parts[1]))
        {
            return OfflineLicenseValidationResult.Fail(OfflineLicenseValidationStatus.Malformed, "离线授权编码格式无效。");
        }

        byte[] payloadBytes;
        byte[] signature;
        try
        {
            payloadBytes = DecodeBase64Url(parts[0]);
            signature = DecodeBase64Url(parts[1]);
        }
        catch (FormatException)
        {
            return OfflineLicenseValidationResult.Fail(OfflineLicenseValidationStatus.Malformed, "离线授权 Base64URL 编码无效。");
        }

        JsonDocument? document = null;
        OfflineLicensePayload? payload = null;
        try
        {
            var json = new UTF8Encoding(false, true).GetString(payloadBytes);
            document = JsonDocument.Parse(json);
            payload = ReadPayload(document.RootElement);
        }
        catch (Exception) when (document is null || payload is null)
        {
            // A tampered encoded payload may no longer be JSON. It still needs to
            // reach signature verification so the fixture is classified correctly.
        }

        var verifierKey = GetPrimaryPublicKey();
        var verifier = new Ed25519Signer();
        verifier.Init(false, verifierKey);
        var encodedPayloadBytes = Encoding.ASCII.GetBytes(parts[0]);
        verifier.BlockUpdate(encodedPayloadBytes, 0, encodedPayloadBytes.Length);
        var signatureValid = verifier.VerifySignature(signature);
        if (!signatureValid)
        {
            document?.Dispose();
            return OfflineLicenseValidationResult.Fail(OfflineLicenseValidationStatus.SignatureInvalid, "离线授权签名无效。");
        }

        if (payload is null || document is null)
        {
            document?.Dispose();
            return OfflineLicenseValidationResult.Fail(OfflineLicenseValidationStatus.Malformed, "离线授权 payload 无效。");
        }

        try
        {
            string canonicalPayload;
            try
            {
                canonicalPayload = Canonicalize(document.RootElement);
            }
            catch (JsonException)
            {
                return OfflineLicenseValidationResult.Fail(OfflineLicenseValidationStatus.Malformed, "离线授权 JSON 规范化失败。");
            }

            if (!CryptographicOperations.FixedTimeEquals(payloadBytes, Encoding.UTF8.GetBytes(canonicalPayload)))
            {
                return OfflineLicenseValidationResult.Fail(OfflineLicenseValidationStatus.Malformed, "离线授权不是规范化 JSON。");
            }

            if (!string.Equals(payload.KeyId, PrimaryKeyId, StringComparison.Ordinal))
            {
                return OfflineLicenseValidationResult.Fail(OfflineLicenseValidationStatus.UnknownKey, "离线授权使用了客户端不信任的签名密钥。");
            }

            if (!string.Equals(payload.ProductId, ProductInfo.AccountProductId, StringComparison.Ordinal) ||
                !string.Equals(payload.Platform, ProductInfo.AccountPlatform, StringComparison.Ordinal))
            {
                return OfflineLicenseValidationResult.Fail(OfflineLicenseValidationStatus.ProductRejected, "离线授权不属于当前产品或平台。");
            }

            if (payload.DeviceKeyHash is null)
            {
                return OfflineLicenseValidationResult.Fail(OfflineLicenseValidationStatus.Malformed, "离线授权缺少设备公钥摘要。");
            }

            if (!string.IsNullOrWhiteSpace(expectedDeviceKeyHash) &&
                !FixedTimeEquals(expectedDeviceKeyHash, payload.DeviceKeyHash))
            {
                return OfflineLicenseValidationResult.Fail(OfflineLicenseValidationStatus.DeviceRejected, "离线授权与当前设备不匹配。");
            }

            var currentTime = now ?? DateTimeOffset.UtcNow;
            if (!string.Equals(payload.LicenseType, "perpetual", StringComparison.OrdinalIgnoreCase) &&
                payload.EntitlementExpiresAt is not null &&
                currentTime >= payload.EntitlementExpiresAt.Value)
            {
                return OfflineLicenseValidationResult.Fail(OfflineLicenseValidationStatus.PolicyExpired, "离线授权已过期。");
            }

            if (payload.HardReauthorizeAt is not null && currentTime >= payload.HardReauthorizeAt.Value)
            {
                return OfflineLicenseValidationResult.Fail(OfflineLicenseValidationStatus.ReauthorizationRequired, "离线授权需要联网重新授权。");
            }

            return OfflineLicenseValidationResult.Success(payload);
        }
        finally
        {
            document.Dispose();
        }
    }

    private Ed25519PrivateKeyParameters GetOrCreatePrivateKey()
    {
        var encoded = _credentialStore.ReadOfflineDevicePrivateKey();
        if (!string.IsNullOrWhiteSpace(encoded))
        {
            try
            {
                var bytes = Convert.FromBase64String(encoded);
                if (bytes.Length == Ed25519PrivateKeyParameters.KeySize)
                {
                    return new Ed25519PrivateKeyParameters(bytes, 0);
                }
            }
            catch (FormatException)
            {
                // A damaged credential is replaced with a new device identity.
            }
        }

        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var privateKey = (Ed25519PrivateKeyParameters)pair.Private;
        _credentialStore.WriteOfflineDevicePrivateKey(Convert.ToBase64String(privateKey.GetEncoded()));
        return privateKey;
    }

    private static Ed25519PublicKeyParameters GetPrimaryPublicKey()
    {
        using var reader = new StringReader(PrimaryPublicKeyPem);
        var pemObject = new PemReader(reader).ReadPemObject()
            ?? throw new CryptographicException("内置离线授权公钥无效。");
        return (Ed25519PublicKeyParameters)PublicKeyFactory.CreateKey(pemObject.Content);
    }

    private static OfflineLicensePayload? ReadPayload(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var licenseId = GetString(root, "licenseId");
        var deviceId = GetString(root, "deviceId");
        var deviceKeyHash = GetString(root, "deviceKeyHash");
        var productId = GetString(root, "productId");
        var platform = GetString(root, "platform");
        var licenseType = GetString(root, "licenseType");
        var keyId = GetString(root, "keyId");
        var issuedAt = GetDateTime(root, "issuedAt");
        if (licenseId is null || deviceId is null || deviceKeyHash is null || productId is null ||
            platform is null || licenseType is null || keyId is null || issuedAt is null)
        {
            return null;
        }

        return new OfflineLicensePayload(
            licenseId,
            deviceId,
            deviceKeyHash,
            productId,
            platform,
            licenseType,
            keyId,
            issuedAt,
            GetNullableDateTime(root, "entitlementExpiresAt"),
            GetNullableDateTime(root, "nextOnlineCheckAt"),
            GetNullableDateTime(root, "hardReauthorizeAt"));
    }

    private static string Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = CanonicalJsonOptions.Encoder,
            Indented = false
        }))
        {
            WriteCanonical(element, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (!property.Name.All(character => character <= 0x7F))
                    {
                        throw new JsonException("授权 payload 字段名必须为 ASCII。");
                    }

                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? GetDateTime(JsonElement root, string name) => GetNullableDateTime(root, name);

    private static DateTimeOffset? GetNullableDateTime(JsonElement root, string name)
    {
        var value = GetString(root, name);
        return value is not null && DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static bool IsBase64Url(string value) =>
        value.Length > 0 && value.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

internal sealed record OfflineLicensePayload(
    string? LicenseId,
    string? DeviceId,
    string? DeviceKeyHash,
    string? ProductId,
    string? Platform,
    string? LicenseType,
    string? KeyId,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? EntitlementExpiresAt,
    DateTimeOffset? NextOnlineCheckAt,
    DateTimeOffset? HardReauthorizeAt);

internal enum OfflineLicenseValidationStatus
{
    Valid,
    Malformed,
    SignatureInvalid,
    UnknownKey,
    ProductRejected,
    DeviceRejected,
    PolicyExpired,
    ReauthorizationRequired
}

internal sealed record OfflineLicenseValidationResult(
    OfflineLicenseValidationStatus Status,
    string Message,
    OfflineLicensePayload? Payload)
{
    public bool IsValid => Status == OfflineLicenseValidationStatus.Valid;

    public static OfflineLicenseValidationResult Success(OfflineLicensePayload payload) =>
        new(OfflineLicenseValidationStatus.Valid, "离线授权有效。", payload);

    public static OfflineLicenseValidationResult Fail(OfflineLicenseValidationStatus status, string message) =>
        new(status, message, null);
}
