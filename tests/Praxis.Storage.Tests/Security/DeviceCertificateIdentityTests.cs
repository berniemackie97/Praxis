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

        VerifiedCertificateIdentity? session = registry.Certificates.AuthenticateForTest(
            certificate, issuer, device);
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
        Assert.Null(registry.Certificates.AuthenticateForTest(
            certificate, issuer, device));
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

        Assert.Null(registry.Certificates.AuthenticateForTest(
            certificate, wrongRoot, device));
        Assert.Null(registry.Certificates.AuthenticateForTest(
            wrongKeyCertificate, issuer, device));
        Assert.Null(registry.Certificates.AuthenticateForTest(
            certificate, issuer, Guid.NewGuid()));
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

        Assert.Null(registry.Certificates.AuthenticateForTest(
            expired, issuer, device));
        Assert.Null(registry.Certificates.AuthenticateForTest(
            longLived, issuer, device));
        Assert.Null(registry.Certificates.AuthenticateForTest(
            serverOnly, issuer, device));
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

        VerifiedCertificateIdentity? secondSession = registry.Certificates.AuthenticateForTest(
            secondCertificate, issuer, secondDevice);
        Assert.NotNull(secondSession);
        DeviceChallenge firstChallenge = registry.DeviceProofs.IssueChallenge(
            firstDevice, space, null, SecurityPermission.Read);
        VerifiedDeviceProof? ownerProof = registry.DeviceProofs.Authenticate(firstChallenge.ChallengeId,
            Sign(firstSigner, firstChallenge.Message));
        Assert.NotNull(ownerProof);

        Assert.False(registry.Certificates.MatchesOperation(secondSession, ownerProof));
        Assert.True(registry.Operator.DisablePrincipal(second));
        Assert.Null(registry.Certificates.AuthenticateForTest(
            secondCertificate, issuer, secondDevice));
    }

    [Fact]
    public void EnrolledOwnerCanReceiveShortLivedCertificateFromSignedRequest()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        using X509Certificate2 issuer = IssueRoot();
        using ECDsa ownerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Guid owner = registry.Operator.CreatePrincipal();
        Guid device = registry.Operator.EnrollDevice(owner, ownerKey.ExportSubjectPublicKeyInfo());
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
        VerifiedDeviceProof proof = Prove(registry, ownerKey, device, space, SecurityPermission.Read);
        byte[] csr = new CertificateRequest("CN=Praxis Client", ownerKey,
            HashAlgorithmName.SHA256).CreateSigningRequest();

        using X509Certificate2 issued = registry.CertificateIssuer.Issue(proof, csr, issuer);
        Assert.False(issued.HasPrivateKey);
        Assert.True(issued.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(2));
        VerifiedCertificateIdentity? identity = registry.Certificates.AuthenticateForTest(
            issued, issuer, device);
        Assert.NotNull(identity);
        Assert.Equal(owner, identity.PrincipalId);
        Assert.True(registry.Certificates.MatchesOperation(identity, proof));
    }

    [Fact]
    public void CertificateIssuerRejectsSubstitutedRequestAndUnapprovedHouseholdDevice()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        using X509Certificate2 issuer = IssueRoot();
        using ECDsa ownerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa memberKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa intruder = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Guid owner = registry.Operator.CreatePrincipal();
        Guid member = registry.Operator.CreatePrincipal();
        Guid ownerDevice = registry.Operator.EnrollDevice(owner, ownerKey.ExportSubjectPublicKeyInfo());
        Guid memberDevice = registry.Operator.EnrollDevice(member, memberKey.ExportSubjectPublicKeyInfo());
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
        registry.Operator.Grant(owner, space, member, SecurityPermission.Read);

        VerifiedDeviceProof ownerProof = Prove(registry, ownerKey, ownerDevice, space, SecurityPermission.Write);
        VerifiedDeviceProof memberProof = Prove(registry, memberKey, memberDevice, space, SecurityPermission.Read);
        byte[] intruderCsr = new CertificateRequest("CN=Praxis Client", intruder,
            HashAlgorithmName.SHA256).CreateSigningRequest();
        byte[] memberCsr = new CertificateRequest("CN=Praxis Client", memberKey,
            HashAlgorithmName.SHA256).CreateSigningRequest();

        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.CertificateIssuer.Issue(ownerProof, intruderCsr, issuer));
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.CertificateIssuer.Issue(memberProof, memberCsr, issuer));

        using RSA memberWrapping = RSA.Create(3072);
        byte[] memberWrapped = memberWrapping.ExportSubjectPublicKeyInfo();
        using ECDsa newMemberKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] applicantSigning = newMemberKey.ExportSubjectPublicKeyInfo();
        byte[] applicantBinding = Sign(newMemberKey,
            DeviceWrappingKey.BindingMessage(applicantSigning, memberWrapped));
        DeviceApprovalChallenge challenge = registry.Enrollment.Begin(ownerProof, member,
            applicantSigning, memberWrapped, applicantBinding);
        ApprovedDeviceIdentity? approval = registry.Enrollment.Complete(ownerProof,
            challenge.ChallengeId, Sign(ownerKey, challenge.Message),
            Sign(newMemberKey, challenge.Message));
        Assert.NotNull(approval);
        VerifiedDeviceProof approvedProof = Prove(registry, newMemberKey, approval.DeviceId,
            space, SecurityPermission.Read);
        byte[] approvedCsr = new CertificateRequest("CN=Praxis Client", newMemberKey,
            HashAlgorithmName.SHA256).CreateSigningRequest();
        using X509Certificate2 issued = registry.CertificateIssuer.Issue(approvedProof,
            approvedCsr, issuer);
        Assert.NotNull(registry.Certificates.AuthenticateForTest(
            issued, issuer, approval.DeviceId));
        Assert.True(registry.Operator.RevokeDevice(approval.DeviceId));
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.CertificateIssuer.Issue(approvedProof, approvedCsr, issuer));
    }

    [Fact]
    public void CertificateIssuerDoesNotAcceptExpiredCaOrMalformedCsr()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        using X509Certificate2 issuer = IssueRoot();
        using ECDsa ownerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Guid owner = registry.Operator.CreatePrincipal();
        Guid device = registry.Operator.EnrollDevice(owner, ownerKey.ExportSubjectPublicKeyInfo());
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
        VerifiedDeviceProof proof = Prove(registry, ownerKey, device, space, SecurityPermission.Read);
        Assert.ThrowsAny<CryptographicException>(() =>
            registry.CertificateIssuer.Issue(proof, RandomNumberGenerator.GetBytes(160), issuer));

        using ECDsa shortKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest shortRequest = new("CN=Short CA", shortKey, HashAlgorithmName.SHA256);
        shortRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        shortRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign, true));
        using X509Certificate2 shortCa = shortRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMinutes(30));
        byte[] csr = new CertificateRequest("CN=Praxis Client", ownerKey,
            HashAlgorithmName.SHA256).CreateSigningRequest();
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.CertificateIssuer.Issue(proof, csr, shortCa));
    }

    private static VerifiedDeviceProof Prove(SecurityRegistry registry, ECDsa signer,
        Guid device, Guid space, SecurityPermission permission)
    {
        DeviceChallenge challenge = registry.DeviceProofs.IssueChallenge(
            device, space, null, permission);
        return registry.DeviceProofs.Authenticate(challenge.ChallengeId,
            Sign(signer, challenge.Message)) ??
            throw new UnauthorizedAccessException("Device proof was rejected.");
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
