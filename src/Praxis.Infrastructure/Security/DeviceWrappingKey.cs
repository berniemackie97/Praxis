using System.Security.Cryptography;
using System.Text;

namespace Praxis.Infrastructure.Security;

/// <summary>Proof that an enrolled signing device controls its RSA-OAEP recipient key.</summary>
public static class DeviceWrappingKey
{
    public static byte[] BindingMessage(ReadOnlySpan<byte> signingPublicKey, ReadOnlySpan<byte> wrappingPublicKey)
    {
        return Encoding.UTF8.GetBytes("Praxis.DeviceWrapping.v1\n" +
            Convert.ToBase64String(signingPublicKey) + "\n" +
            Convert.ToBase64String(wrappingPublicKey) + "\n");
    }

    public static bool Verify(ReadOnlySpan<byte> signingPublicKey,
        ReadOnlySpan<byte> wrappingPublicKey, ReadOnlySpan<byte> signature)
    {
        if (signingPublicKey.Length is < 80 or > 128 ||
            wrappingPublicKey.Length is < 350 or > 600 || signature.Length != 64)
        {
            return false;
        }

        try
        {
            using ECDsa signer = ECDsa.Create();
            signer.ImportSubjectPublicKeyInfo(signingPublicKey, out int signingLength);
            if (signingLength != signingPublicKey.Length || signer.KeySize != 256 ||
                signer.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            {
                return false;
            }

            using RSA recipient = RSA.Create();
            recipient.ImportSubjectPublicKeyInfo(wrappingPublicKey, out int wrappingLength);
            if (wrappingLength != wrappingPublicKey.Length || recipient.KeySize != 3072)
            {
                return false;
            }

            return signer.VerifyData(BindingMessage(signingPublicKey, wrappingPublicKey),
                signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
