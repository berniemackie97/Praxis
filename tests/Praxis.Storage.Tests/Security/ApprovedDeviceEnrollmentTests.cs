using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Praxis.Storage.Security;
using Praxis.Storage.Tests.Support;
using Xunit;

namespace Praxis.Storage.Tests.Security;

public sealed class ApprovedDeviceEnrollmentTests : IDisposable
{
    private readonly string root = Path.Combine(TestPaths.Temp,
        "praxis-approved-devices-" + Guid.NewGuid().ToString("N"));
    private readonly string trustDirectory = Path.Combine(TestPaths.Temp,
        "praxis-approved-pins-" + Guid.NewGuid().ToString("N"));

    public ApprovedDeviceEnrollmentTests()
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(trustDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(trustDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void OwnerApprovesHouseholdDeviceAndClientPinsItAcrossRestart()
    {
        Guid installation, ownerDevice, memberDevice, space;
        byte[] memberSigningKey;
        using ECDsa ownerSigner = Signer();
        using ECDsa memberSigner = Signer();
        using RSA ownerRecipient = Recipient();
        using RSA memberRecipient = Recipient();

        using (SecurityRegistry registry = SecurityRegistry.Open(root))
        {
            installation = registry.InstallationId;
            Guid owner = registry.Operator.CreatePrincipal();
            Guid member = registry.Operator.CreatePrincipal();
            ownerDevice = Enroll(registry, owner, ownerSigner, ownerRecipient);
            space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
            registry.Operator.Grant(owner, space, member, SecurityPermission.Read);
            VerifiedDeviceProof ownerWrite = Prove(registry, ownerSigner, ownerDevice,
                space, null, SecurityPermission.Write);

            DeviceApprovalChallenge challenge = registry.Enrollment.Begin(ownerWrite, member,
                memberSigner.ExportSubjectPublicKeyInfo(),
                memberRecipient.ExportSubjectPublicKeyInfo(),
                Binding(memberSigner, memberRecipient));
            ApprovedDeviceIdentity? approved = registry.Enrollment.Complete(ownerWrite,
                challenge.ChallengeId, Sign(ownerSigner, challenge.Message),
                Sign(memberSigner, challenge.Message));
            Assert.NotNull(approved);
            memberDevice = approved.DeviceId;
            memberSigningKey = approved.SigningPublicKey;
            Assert.Equal(challenge.DeviceId, memberDevice);
            Assert.True(ApprovedDeviceEnrollment.VerifyApproval(approved,
                ownerSigner.ExportSubjectPublicKeyInfo(), DateTimeOffset.UtcNow));

            using ClientDeviceTrustStore client = ClientDeviceTrustStore.Open(trustDirectory,
                installation, ownerDevice, ownerSigner.ExportSubjectPublicKeyInfo());
            client.Accept(approved, DateTimeOffset.UtcNow);
            IReadOnlyDictionary<Guid, byte[]> trusted = client.TrustedRecipients(space);
            Assert.Equal(2, trusted.Count);
            Assert.Equal(memberSigningKey, trusted[memberDevice]);

            SpaceKeyPublication publication = ClientSpaceKeys.Protect(installation, space, 1,
                ownerDevice, ownerSigner, registry.SpaceKeys.Recipients(ownerWrite),
                trusted, ClientSpaceKeys.GenerateContentKey(),
                ClientSpaceKeys.GenerateRecoverySecret());
            registry.SpaceKeys.Publish(ownerWrite, publication);
            Assert.NotNull(registry.SpaceKeys.Download(
                Prove(registry, memberSigner, memberDevice, space, null, SecurityPermission.Read)));
        }

        using (ClientDeviceTrustStore reopened = ClientDeviceTrustStore.Open(trustDirectory,
            installation, ownerDevice, ownerSigner.ExportSubjectPublicKeyInfo()))
        {
            Assert.Equal(memberSigningKey, reopened.TrustedRecipients(space)[memberDevice]);
            using ECDsa publicOnly = ECDsa.Create();
            publicOnly.ImportSubjectPublicKeyInfo(ownerSigner.ExportSubjectPublicKeyInfo(), out _);
            Assert.Throws<UnauthorizedAccessException>(() =>
                reopened.Revoke(space, memberDevice, publicOnly, DateTimeOffset.UtcNow));
            Assert.Contains(memberDevice, reopened.TrustedRecipients(space).Keys);
            Assert.True(reopened.Revoke(space, memberDevice, ownerSigner, DateTimeOffset.UtcNow));
            Assert.DoesNotContain(memberDevice, reopened.TrustedRecipients(space).Keys);
        }

        using (ClientDeviceTrustStore final = ClientDeviceTrustStore.Open(trustDirectory,
            installation, ownerDevice, ownerSigner.ExportSubjectPublicKeyInfo()))
        {
            Assert.DoesNotContain(memberDevice, final.TrustedRecipients(space).Keys);
        }
    }

    [Fact]
    public void WrongApproverOrApplicantSignatureConsumesAttemptWithoutEnrollment()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        Guid member = registry.Operator.CreatePrincipal();
        using ECDsa ownerSigner = Signer();
        using ECDsa memberSigner = Signer();
        using ECDsa intruder = Signer();
        using RSA ownerRecipient = Recipient();
        using RSA memberRecipient = Recipient();
        Guid ownerDevice = Enroll(registry, owner, ownerSigner, ownerRecipient);
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
        registry.Operator.Grant(owner, space, member, SecurityPermission.Read);

        VerifiedDeviceProof ownerProof = Prove(registry, ownerSigner, ownerDevice,
            space, null, SecurityPermission.Write);
        DeviceApprovalChallenge wrongOwner = registry.Enrollment.Begin(ownerProof, member,
            memberSigner.ExportSubjectPublicKeyInfo(), memberRecipient.ExportSubjectPublicKeyInfo(),
            Binding(memberSigner, memberRecipient));
        Assert.Null(registry.Enrollment.Complete(ownerProof, wrongOwner.ChallengeId,
            Sign(intruder, wrongOwner.Message), Sign(memberSigner, wrongOwner.Message)));
        Assert.Null(registry.Enrollment.Complete(ownerProof, wrongOwner.ChallengeId,
            Sign(ownerSigner, wrongOwner.Message), Sign(memberSigner, wrongOwner.Message)));

        DeviceApprovalChallenge wrongApplicant = registry.Enrollment.Begin(ownerProof, member,
            memberSigner.ExportSubjectPublicKeyInfo(), memberRecipient.ExportSubjectPublicKeyInfo(),
            Binding(memberSigner, memberRecipient));
        Assert.Null(registry.Enrollment.Complete(ownerProof, wrongApplicant.ChallengeId,
            Sign(ownerSigner, wrongApplicant.Message), Sign(intruder, wrongApplicant.Message)));
        Assert.Null(registry.Enrollment.Complete(ownerProof, wrongApplicant.ChallengeId,
            Sign(ownerSigner, wrongApplicant.Message), Sign(memberSigner, wrongApplicant.Message)));
    }

    [Fact]
    public void UnauthorizedMemberOrRevokedMembershipCannotAdmitNewDevice()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        Guid member = registry.Operator.CreatePrincipal();
        using ECDsa ownerSigner = Signer();
        using ECDsa memberSigner = Signer();
        using ECDsa applicantSigner = Signer();
        using RSA ownerRecipient = Recipient();
        using RSA memberRecipient = Recipient();
        using RSA applicantRecipient = Recipient();
        Guid ownerDevice = Enroll(registry, owner, ownerSigner, ownerRecipient);
        Guid memberDevice = Enroll(registry, member, memberSigner, memberRecipient);
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
        registry.Operator.Grant(owner, space, member, SecurityPermission.Read | SecurityPermission.Write);
        VerifiedDeviceProof memberWrite = Prove(registry, memberSigner, memberDevice,
            space, null, SecurityPermission.Write);

        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.Enrollment.Begin(memberWrite, member,
                applicantSigner.ExportSubjectPublicKeyInfo(),
                applicantRecipient.ExportSubjectPublicKeyInfo(),
                Binding(applicantSigner, applicantRecipient)));

        VerifiedDeviceProof ownerWrite = Prove(registry, ownerSigner, ownerDevice,
            space, null, SecurityPermission.Write);
        DeviceApprovalChallenge pending = registry.Enrollment.Begin(ownerWrite, member,
            applicantSigner.ExportSubjectPublicKeyInfo(),
            applicantRecipient.ExportSubjectPublicKeyInfo(),
            Binding(applicantSigner, applicantRecipient));
        Assert.True(registry.Operator.RevokeGrant(owner, space, member));
        Assert.Null(registry.Enrollment.Complete(ownerWrite, pending.ChallengeId,
            Sign(ownerSigner, pending.Message), Sign(applicantSigner, pending.Message)));
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.Enrollment.Begin(ownerWrite, member,
                applicantSigner.ExportSubjectPublicKeyInfo(),
                applicantRecipient.ExportSubjectPublicKeyInfo(),
                Binding(applicantSigner, applicantRecipient)));
    }

    [Fact]
    public void ForeignOwnerPinsAndHostAddedKeysNeverBecomeTrustedRecipients()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        Guid member = registry.Operator.CreatePrincipal();
        using ECDsa ownerSigner = Signer();
        using ECDsa impostorSigner = Signer();
        using ECDsa memberSigner = Signer();
        using RSA ownerRecipient = Recipient();
        using RSA memberRecipient = Recipient();
        Guid ownerDevice = Enroll(registry, owner, ownerSigner, ownerRecipient);
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
        registry.Operator.Grant(owner, space, member, SecurityPermission.Read);
        VerifiedDeviceProof ownerWrite = Prove(registry, ownerSigner, ownerDevice,
            space, null, SecurityPermission.Write);

        DeviceApprovalChallenge challenge = registry.Enrollment.Begin(ownerWrite, member,
            memberSigner.ExportSubjectPublicKeyInfo(),
            memberRecipient.ExportSubjectPublicKeyInfo(),
            Binding(memberSigner, memberRecipient));
        ApprovedDeviceIdentity? approved = registry.Enrollment.Complete(ownerWrite,
            challenge.ChallengeId, Sign(ownerSigner, challenge.Message),
            Sign(memberSigner, challenge.Message));
        Assert.NotNull(approved);
        Assert.False(ApprovedDeviceEnrollment.VerifyApproval(approved,
            impostorSigner.ExportSubjectPublicKeyInfo(), DateTimeOffset.UtcNow));

        using ClientDeviceTrustStore local = ClientDeviceTrustStore.Open(trustDirectory,
            registry.InstallationId, ownerDevice, ownerSigner.ExportSubjectPublicKeyInfo());
        Assert.Throws<UnauthorizedAccessException>(() =>
            local.Accept(approved with { OwnerSignature = Sign(impostorSigner, challenge.Message) },
                DateTimeOffset.UtcNow));
        Assert.Single(local.TrustedRecipients(space));
        Assert.Throws<UnauthorizedAccessException>(() =>
            ClientSpaceKeys.Protect(registry.InstallationId, space, 1,
                ownerDevice, ownerSigner, registry.SpaceKeys.Recipients(ownerWrite),
                local.TrustedRecipients(space), ClientSpaceKeys.GenerateContentKey(),
                ClientSpaceKeys.GenerateRecoverySecret()));

        Assert.Throws<UnauthorizedAccessException>(() =>
            local.Revoke(space, approved.DeviceId, impostorSigner, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ClientTrustDirectoryRejectsDifferentOwnerAndUnsafePermissions()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        using ECDsa ownerSigner = Signer();
        using ECDsa impostor = Signer();
        Guid ownerDevice = Guid.NewGuid();
        using (ClientDeviceTrustStore first = ClientDeviceTrustStore.Open(trustDirectory,
            registry.InstallationId, ownerDevice, ownerSigner.ExportSubjectPublicKeyInfo()))
        {
            Assert.Single(first.TrustedRecipients(Guid.NewGuid()));
        }

        Assert.Throws<UnauthorizedAccessException>(() => ClientDeviceTrustStore.Open(
            trustDirectory, registry.InstallationId, ownerDevice,
            impostor.ExportSubjectPublicKeyInfo()));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(trustDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute | UnixFileMode.GroupRead);
            Assert.Throws<UnauthorizedAccessException>(() =>
                ClientDeviceTrustStore.Open(trustDirectory,
                    registry.InstallationId, ownerDevice, ownerSigner.ExportSubjectPublicKeyInfo()));
            File.SetUnixFileMode(trustDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void MismatchedCertificateIdentityPermissionSourceAndEpochNeverRunDataOperation()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        using ECDsa signer = Signer();
        using RSA recipient = Recipient();
        Guid ownerDevice = Enroll(registry, owner, signer, recipient);
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
        Guid source = Guid.NewGuid();
        Guid foreign = Guid.NewGuid();
        registry.Operator.AssignSource(owner, space, source);
        registry.Operator.AssignSource(owner, space, foreign);

        VerifiedDeviceProof write = Prove(registry, signer, ownerDevice,
            space, null, SecurityPermission.Write);
        byte[] contentKey = ClientSpaceKeys.GenerateContentKey();
        SpaceKeyPublication first = ClientSpaceKeys.Protect(registry.InstallationId, space, 1,
            ownerDevice, signer, registry.SpaceKeys.Recipients(write),
            new Dictionary<Guid, byte[]> { [ownerDevice] = signer.ExportSubjectPublicKeyInfo() },
            contentKey, ClientSpaceKeys.GenerateRecoverySecret());
        registry.SpaceKeys.Publish(write, first);

        using X509Certificate2 rootCertificate = IssueRoot();
        using X509Certificate2 leaf = IssueClient(rootCertificate, signer);
        VerifiedCertificateIdentity? identity = registry.Certificates.AuthenticateForTest(
            leaf, rootCertificate, ownerDevice);
        Assert.NotNull(identity);
        VerifiedDeviceProof read = Prove(registry, signer, ownerDevice,
            space, source, SecurityPermission.Read);
        int executions = 0;
        int value = registry.ProtectedData.Execute(identity, read, source,
            SecurityPermission.Read, 1, () => { executions++; return 42; });
        Assert.Equal(42, value);
        Assert.Equal(1, executions);
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.ProtectedData.Execute(identity, read, foreign,
                SecurityPermission.Read, 1, () => { executions++; return 7; }));
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.ProtectedData.Execute(identity, read, source,
                SecurityPermission.Write, 1, () => { executions++; return 7; }));
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.ProtectedData.Execute(identity, read, source,
                SecurityPermission.Read, 2, () => { executions++; return 7; }));
        Assert.Equal(1, executions);

        using ECDsa anotherSigner = Signer();
        using RSA anotherRecipient = Recipient();
        // A newly enrolled owner device fences publication until the next key epoch.
        Guid newOwnerDevice = Enroll(registry, owner, anotherSigner, anotherRecipient);
        Assert.NotEqual(Guid.Empty, newOwnerDevice);
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.ProtectedData.Execute(identity, read, source,
                SecurityPermission.Read, 1, () => { executions++; return 7; }));
        Assert.Equal(1, executions);
    }

    private static ECDsa Signer() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static RSA Recipient()
    {
        RSA recipient = RSA.Create();
        recipient.KeySize = 3072;
        return recipient;
    }

    private static byte[] Sign(ECDsa signer, byte[] data) =>
        signer.SignData(data, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    private static byte[] Binding(ECDsa signer, RSA recipient) =>
        Sign(signer, DeviceWrappingKey.BindingMessage(
            signer.ExportSubjectPublicKeyInfo(), recipient.ExportSubjectPublicKeyInfo()));

    private static Guid Enroll(SecurityRegistry registry, Guid principal, ECDsa signer, RSA recipient) =>
        registry.Operator.EnrollDevice(principal, signer.ExportSubjectPublicKeyInfo(),
            recipient.ExportSubjectPublicKeyInfo(), Binding(signer, recipient));

    private static VerifiedDeviceProof Prove(SecurityRegistry registry, ECDsa signer,
        Guid deviceId, Guid space, Guid? source, SecurityPermission permission)
    {
        DeviceChallenge challenge = registry.DeviceProofs.IssueChallenge(deviceId,
            space, source, permission);
        return registry.DeviceProofs.Authenticate(challenge.ChallengeId,
            Sign(signer, challenge.Message)) ??
            throw new UnauthorizedAccessException("The enrolled device proof was rejected.");
    }

    private static X509Certificate2 IssueRoot()
    {
        using ECDsa signer = Signer();
        CertificateRequest request = new("CN=Praxis Test Root", signer, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2),
            DateTimeOffset.UtcNow.AddDays(2));
    }

    private static X509Certificate2 IssueClient(X509Certificate2 rootCert, ECDsa signer)
    {
        CertificateRequest request = new("CN=Praxis Test Client", signer, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature, true));
        OidCollection purposes = new() { new Oid("1.3.6.1.5.5.7.3.2") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(purposes, true));
        return request.Create(rootCert, DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        if (Directory.Exists(trustDirectory))
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(trustDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            Directory.Delete(trustDirectory, recursive: true);
        }
    }
}
