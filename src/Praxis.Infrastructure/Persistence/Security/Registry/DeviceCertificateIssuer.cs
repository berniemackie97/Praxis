using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.Sqlite;
using Praxis.Domain.Security;

namespace Praxis.Infrastructure.Persistence.Security.Registry;

/// <summary>
/// Offline/operator-only certificate issuance. The CA private key is supplied by the
/// operator for this call; Praxis does not store it or expose a certificate endpoint.
/// </summary>
public sealed class DeviceCertificateIssuer(SecurityRegistry registry)
{
    private static readonly TimeSpan Validity = TimeSpan.FromHours(24);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

    public X509Certificate2 Issue(VerifiedDeviceProof proof,
        ReadOnlySpan<byte> signedPkcs10, X509Certificate2 issuerCertificate)
    {
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(issuerCertificate);
        if (signedPkcs10.Length is < 100 or > 8192 ||
            proof.SourceId is not null ||
            proof.Permission is not (SecurityPermission.Read or SecurityPermission.Write))
        {
            throw new UnauthorizedAccessException("Device certificate enrollment is not authorized.");
        }

        byte[] csr = signedPkcs10.ToArray();
        CertificateRequest request = CertificateRequest.LoadSigningRequest(
            csr, HashAlgorithmName.SHA256);
        using ECDsa? requestPublicKey = request.PublicKey.GetECDsaPublicKey();
        if (requestPublicKey is null || requestPublicKey.KeySize != 256 ||
            requestPublicKey.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" ||
            request.CertificateExtensions.Count != 0)
        {
            throw new UnauthorizedAccessException("A signed P-256 device request is required.");
        }

        byte[] requestedKey = requestPublicKey.ExportSubjectPublicKeyInfo();
        return registry.Execute(connection =>
        {
            if (!registry.DeviceProofs.IsCurrent(proof))
            {
                throw new UnauthorizedAccessException("The enrolled device proof is no longer current.");
            }

            using SqliteCommand device = SecurityRegistry.Command(connection, null, """
                SELECT d.public_key
                FROM devices d
                JOIN principals p ON p.principal_id=d.principal_id AND p.disabled_utc IS NULL
                JOIN security_spaces s ON s.space_id=$space
                WHERE d.device_id=$device AND d.principal_id=$principal AND d.revoked_utc IS NULL
                  AND (s.owner_id=d.principal_id OR EXISTS (
                       SELECT 1 FROM device_approvals a
                       WHERE a.device_id=d.device_id AND a.space_id=s.space_id))
                """,
                ("$space", SecurityRegistry.Id(proof.SpaceId)),
                ("$device", SecurityRegistry.Id(proof.DeviceId)),
                ("$principal", SecurityRegistry.Id(proof.PrincipalId)));
            if (device.ExecuteScalar() is not byte[] enrolled ||
                !CryptographicOperations.FixedTimeEquals(enrolled, requestedKey))
            {
                throw new UnauthorizedAccessException("The device signing key is not approved for this space.");
            }

            DateTimeOffset now = registry.UtcNow;
            DateTimeOffset start = now.Subtract(ClockSkew);
            DateTimeOffset end = now.Add(Validity);
            using ECDsa? caPublicKey = issuerCertificate.GetECDsaPublicKey();
            if (!issuerCertificate.HasPrivateKey || caPublicKey is null ||
                caPublicKey.KeySize != 256 ||
                caPublicKey.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" ||
                new DateTimeOffset(issuerCertificate.NotBefore.ToUniversalTime()) > start ||
                new DateTimeOffset(issuerCertificate.NotAfter.ToUniversalTime()) <= end ||
                !issuerCertificate.Extensions.OfType<X509BasicConstraintsExtension>()
                    .Any(x => x.CertificateAuthority) ||
                !issuerCertificate.Extensions.OfType<X509KeyUsageExtension>()
                    .Any(x => (x.KeyUsages & X509KeyUsageFlags.KeyCertSign) != 0))
            {
                throw new UnauthorizedAccessException("A valid operator-held signing CA is required.");
            }

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature, true));
            OidCollection usage = new() { new Oid(ClientAuthenticationOid) };
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usage, true));
            request.CertificateExtensions.Add(
                new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            return request.Create(issuerCertificate, start, end, RandomNumberGenerator.GetBytes(16));
        });
    }
}
