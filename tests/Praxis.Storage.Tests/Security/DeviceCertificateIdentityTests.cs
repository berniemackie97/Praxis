using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Praxis.Storage.Security;
using Praxis.Storage.Tests.Support;
using Xunit;

namespace Praxis.Storage.Tests.Security;

public sealed class DeviceCertificateIdentityTests : IDisposable
{
    private readonly string root = Path.Combine(TestPaths.Temp,
        "praxis-cert-" + Guid.NewGuid().ToString("N"));

    public DeviceCertificateIdentityTests()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void PinnedIssuerAndEnrolledDeviceMustMatchCurrentSignedOperation()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        using X509Certificate2 issuer = IssueRoot();
        using ECDsa enrolled = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Guid owner = registry.Operator.CreatePrincipal();
        Guid device = registry.Operator.EnrollDevice(owner, enrolled.ExportSubjectPublicKeyInfo());
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
        using X509Certificate2 certificate = IssueClient(issuer, enrolled);

        VerifiedCertificateIdentity? session = registry.Certificates.Authenticate(
            certificate, issuer, device, X509RevocationMode.NoCheck);
        Assert.NotNull(session);
        Assert.Equal(owner, session.PrincipalId);
        Assert.Equal(device, session.DeviceId);
        DeviceChallenge challenge = registry.DeviceProofs.IssueChallenge(
            device, space, null, SecurityPermission.Read);
        VerifiedDeviceProof? proof = registry.DeviceProofs.Authenticate(challenge.ChallengeId,
            Sign(enrolled, challenge.Message));
        Assert.NotNull(proof);
        Assert.True(registry.Certificates.MatchesOperation(session, proof));

        Assert.True(registry.Operator.RevokeDevice(device));
        Assert.Null(registry.Certificates.Authenticate(
            certificate, issuer, device, X509RevocationMode.NoCheck));
        Assert.False(registry.Certificates.MatchesOperation(session, proof));
    }

    [Fact]
    public void WrongIssuerOrUnenrolledSigningKeyIsDeniedRegardlessOfCertificateSubject()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        using X509Certificate2 issuer = IssueRoot();
        using X509Certificate2 wrongRoot = IssueRoot();
        using ECDsa enrolled = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Guid principal = registry.Operator.CreatePrincipal();
        Guid device = registry.Operator.EnrollDevice(principal, enrolled.ExportSubjectPublicKeyInfo());
        using X509Certificate2 certificate = IssueClient(issuer, enrolled);
        using X509Certificate2 wrongKeyCertificate = IssueClient(issuer, attacker);

        Assert.Null(registry.Certificates.Authenticate(
            certificate, wrongRoot, device, X509RevocationMode.NoCheck));
        Assert.Null(registry.Certificates.Authenticate(
            wrongKeyCertificate, issuer, device, X509RevocationMode.NoCheck));
        Assert.Null(registry.Certificates.Authenticate(
            certificate, issuer, Guid.NewGuid(), X509RevocationMode.NoCheck));
    }

    [Fact]
    public void ExpiredOrLongLivedOrNonClientCertificatesAreRejected()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        using X509Certificate2 issuer = IssueRoot();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Guid principal = registry.Operator.CreatePrincipal();
        Guid device = registry.Operator.EnrollDevice(principal, key.ExportSubjectPublicKeyInfo());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 expired = IssueClient(issuer, key,
            now.AddDays(-5), now.AddDays(-1));
        using X509Certificate2 longLived = IssueClient(issuer, key,
            now.AddDays(-1), now.AddDays(8));
        using X509Certificate2 serverOnly = IssueClient(issuer, key,
            now.AddMinutes(-5), now.AddHours(1), clientAuth: false);

        Assert.Null(registry.Certificates.Authenticate(
            expired, issuer, device, X509RevocationMode.NoCheck));
        Assert.Null(registry.Certificates.Authenticate(
            longLived, issuer, device, X509RevocationMode.NoCheck));
        Assert.Null(registry.Certificates.Authenticate(
            serverOnly, issuer, device, X509RevocationMode.NoCheck));
    }

    [Fact]
    public void CertificateOfDifferentEnrolledDeviceCannotBorrowAnotherDevicesProof()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        using X509Certificate2 issuer = IssueRoot();
        using ECDsa firstSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa secondSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Guid first = registry.Operator.CreatePrincipal();
        Guid second = registry.Operator.CreatePrincipal();
        Guid firstDevice = registry.Operator.EnrollDevice(first, firstSigner.ExportSubjectPublicKeyInfo());
        Guid secondDevice = registry.Operator.EnrollDevice(second, secondSigner.ExportSubjectPublicKeyInfo());
        Guid space = registry.Operator.CreateSpace(first, SecurityProcessingClass.TrustedHost);
        registry.Operator.Grant(first, space, second, SecurityPermission.Read);
        using X509Certificate2 secondCertificate = IssueClient(issuer, secondSigner);

        VerifiedCertificateIdentity? secondSession = registry.Certificates.Authenticate(
            secondCertificate, issuer, secondDevice, X509RevocationMode.NoCheck);
        Assert.NotNull(secondSession);
        DeviceChallenge firstChallenge = registry.DeviceProofs.IssueChallenge(
            firstDevice, space, null, SecurityPermission.Read);
        VerifiedDeviceProof? ownerProof = registry.DeviceProofs.Authenticate(firstChallenge.ChallengeId,
            Sign(firstSigner, firstChallenge.Message));
        Assert.NotNull(ownerProof);

        Assert.False(registry.Certificates.MatchesOperation(secondSession, ownerProof));
        Assert.True(registry.Operator.DisablePrincipal(second));
        Assert.Null(registry.Certificates.Authenticate(
            secondCertificate, issuer, secondDevice, X509RevocationMode.NoCheck));
    }

    private static X509Certificate2 IssueRoot()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=Praxis Test CA", signer, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-30), now.AddDays(30));
    }

    private static X509Certificate2 IssueClient(X509Certificate2 issuer, ECDsa key,
        DateTimeOffset? starts = null, DateTimeOffset? ends = null, bool clientAuth = true)
    {
        CertificateRequest request = new("CN=Praxis Client", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature, true));
        OidCollection usages = new()
        {
            new Oid(clientAuth ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1")
        };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
        return request.Create(issuer,
            starts ?? DateTimeOffset.UtcNow.AddMinutes(-5),
            ends ?? DateTimeOffset.UtcNow.AddHours(1),
            RandomNumberGenerator.GetBytes(16));
    }

    private static byte[] Sign(ECDsa signer, byte[] message) =>
        signer.SignData(message, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
