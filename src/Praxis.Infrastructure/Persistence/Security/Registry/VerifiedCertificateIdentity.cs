namespace Praxis.Infrastructure.Persistence.Security.Registry;

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
