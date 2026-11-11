using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Praxis.Infrastructure.Security.Cryptography;

public static class PinnedCertificateChain
{
    public const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";
    public const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    public static bool Verify(X509Certificate2 certificate,
        X509Certificate2 pinnedRoot, string usageOid)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(pinnedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(usageOid);
        using X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(pinnedRoot);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(usageOid));
        return chain.Build(certificate) && chain.ChainElements.Count >= 2 &&
            CryptographicOperations.FixedTimeEquals(
                chain.ChainElements[^1].Certificate.RawData, pinnedRoot.RawData);
    }
}
