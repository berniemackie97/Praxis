using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.Sqlite;

namespace Praxis.Infrastructure.Security;

/// <summary>
/// The identity of a client whose certificate has been validated against the
/// configured private CA and whose public signing key matches an enrolled device.
/// Only a real authenticated TLS connection may supply the certificate.
/// </summary>
public sealed class VerifiedCertificateIdentity
{
    internal VerifiedCertificateIdentity(Guid principalId, Guid deviceId,
        DateTimeOffset certificateExpiresUtc)
    {
        PrincipalId = principalId;
        DeviceId = deviceId;
        CertificateExpiresUtc = certificateExpiresUtc;
    }

    public Guid PrincipalId { get; }
    public Guid DeviceId { get; }
    public DateTimeOffset CertificateExpiresUtc { get; }
}

/// <summary>
/// Verifies X.509 client-authentication certificates against a separately pinned CA.
/// Certificate subject strings never confer Praxis principal or security-space rights.
/// </summary>
public sealed class DeviceCertificateIdentity(SecurityRegistry registry)
{
    private static readonly TimeSpan MaxCertificateLifetime = TimeSpan.FromDays(7);
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

    public VerifiedCertificateIdentity? Authenticate(
        X509Certificate2 clientCertificate, X509Certificate2 trustedRoot, Guid deviceId) =>
        Inspect(clientCertificate, trustedRoot, deviceId, X509RevocationMode.Online);

    internal VerifiedCertificateIdentity? AuthenticateForTest(
        X509Certificate2 clientCertificate, X509Certificate2 trustedRoot, Guid deviceId) =>
        Inspect(clientCertificate, trustedRoot, deviceId, X509RevocationMode.NoCheck);

    private VerifiedCertificateIdentity? Inspect(
        X509Certificate2 clientCertificate, X509Certificate2 trustedRoot,
        Guid deviceId, X509RevocationMode revocationMode)
    {
        ArgumentNullException.ThrowIfNull(clientCertificate);
        ArgumentNullException.ThrowIfNull(trustedRoot);
        if (deviceId == Guid.Empty)
        {
            return null;
        }

        DateTimeOffset now = registry.UtcNow;
        DateTimeOffset notBefore = new(clientCertificate.NotBefore.ToUniversalTime());
        DateTimeOffset notAfter = new(clientCertificate.NotAfter.ToUniversalTime());
        if (notBefore > now || notAfter <= now ||
            notAfter - notBefore > MaxCertificateLifetime ||
            clientCertificate.Extensions.OfType<X509BasicConstraintsExtension>()
                .Any(x => x.CertificateAuthority))
        {
            return null;
        }

        bool hasUsage = clientCertificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .Any(x => x.EnhancedKeyUsages.Cast<Oid>()
                .Any(oid => oid.Value == ClientAuthenticationOid));
        bool canSign = clientCertificate.Extensions.OfType<X509KeyUsageExtension>()
            .Any(x => (x.KeyUsages & X509KeyUsageFlags.DigitalSignature) != 0);
        if (!hasUsage || !canSign)
        {
            return null;
        }

        using X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ClientAuthenticationOid));
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.RevocationMode = revocationMode;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        if (!chain.Build(clientCertificate) || chain.ChainElements.Count < 2 ||
            !CryptographicOperations.FixedTimeEquals(
                chain.ChainElements[^1].Certificate.RawData, trustedRoot.RawData))
        {
            return null;
        }

        using ECDsa? certificateKey = clientCertificate.GetECDsaPublicKey();
        if (certificateKey is null || certificateKey.KeySize != 256)
        {
            return null;
        }

        byte[] publicKey = certificateKey.ExportSubjectPublicKeyInfo();
        return registry.Execute(connection =>
        {
            using SqliteCommand command = SecurityRegistry.Command(connection, null, """
                SELECT d.principal_id, d.public_key
                FROM devices d
                JOIN principals p ON p.principal_id=d.principal_id AND p.disabled_utc IS NULL
                WHERE d.device_id=$device AND d.revoked_utc IS NULL
                """, ("$device", SecurityRegistry.Id(deviceId)));
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read() ||
                !CryptographicOperations.FixedTimeEquals(
                    (byte[])reader.GetValue(1), publicKey))
            {
                return null;
            }

            return new VerifiedCertificateIdentity(
                Guid.Parse(reader.GetString(0)), deviceId, notAfter);
        });
    }

    /// <summary>
    /// Session-to-operation binding: mTLS identity and signed operation proof must
    /// name the very same still-authorized device and principal.
    /// </summary>
    public bool MatchesOperation(VerifiedCertificateIdentity session, VerifiedDeviceProof proof)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(proof);
        return session.CertificateExpiresUtc > registry.UtcNow &&
            session.DeviceId == proof.DeviceId &&
            session.PrincipalId == proof.PrincipalId &&
            registry.DeviceProofs.IsCurrent(proof);
    }
}
