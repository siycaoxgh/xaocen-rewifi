using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace XAOCEN.ReWiFi;

/// <summary>
/// Maintains the stable Ed25519 identity used only for Account device registration.
/// The existing Credential Manager target is intentionally reused across upgrades.
/// </summary>
internal sealed class DeviceIdentityService
{
    private readonly WindowsCredentialStore _credentialStore;

    public DeviceIdentityService(WindowsCredentialStore? credentialStore = null)
    {
        _credentialStore = credentialStore ?? new WindowsCredentialStore();
    }

    public string GetOrCreatePublicKey()
    {
        var privateKey = GetOrCreatePrivateKey();
        var publicKey = privateKey.GeneratePublicKey();
        var subjectPublicKeyInfo = SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(publicKey);
        return PemEncoding.WriteString("PUBLIC KEY", subjectPublicKeyInfo.GetDerEncoded());
    }

    private Ed25519PrivateKeyParameters GetOrCreatePrivateKey()
    {
        var encoded = _credentialStore.ReadDeviceRegistrationPrivateKey();
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
                AppLogger.Warning("设备登记密钥格式损坏，将为当前 Windows 用户重新生成。");
            }
        }

        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var privateKey = (Ed25519PrivateKeyParameters)pair.Private;
        _credentialStore.WriteDeviceRegistrationPrivateKey(Convert.ToBase64String(privateKey.GetEncoded()));
        return privateKey;
    }
}
