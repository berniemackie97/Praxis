using System.Security.Cryptography;
using Praxis.Infrastructure.Security;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Security;

public sealed class RecoveryEnrollmentTests : IDisposable
{
    private readonly string root = Path.Combine(TestPaths.Temp,
        "praxis-recovery-" + Guid.NewGuid().ToString("N"));

    public RecoveryEnrollmentTests()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void LostLastDeviceReenrollsAndRecoversHistoricalSpaceKeyAfterRestart()
    {
        Guid owner, space, lostDevice, installation;
        byte[] oldKey = ClientSpaceKeys.GenerateContentKey();
        byte[] contentRecovery = ClientSpaceKeys.GenerateRecoverySecret();
        using ECDsa originalSigner = Signer();
        using RSA originalRecipient = Recipient();
        using ECDsa offlineRecoverySigner = Signer();

        using (SecurityRegistry registry = SecurityRegistry.Open(root))
        {
            installation = registry.InstallationId;
            owner = registry.Operator.CreatePrincipal();
            lostDevice = Enroll(registry, owner, originalSigner, originalRecipient);
            space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
            VerifiedDeviceProof ownerWrite = Prove(registry, originalSigner, lostDevice,
                space, SecurityPermission.Write);
            Register(registry, ownerWrite, originalSigner, offlineRecoverySigner);
            SpaceKeyPublication first = ClientSpaceKeys.Protect(installation, space, 1,
                lostDevice, originalSigner, registry.SpaceKeys.Recipients(ownerWrite),
                Trusted((lostDevice, originalSigner)), oldKey, contentRecovery);
            registry.SpaceKeys.Publish(ownerWrite, first);
            Assert.True(registry.Operator.RevokeDevice(lostDevice));
        }

        using ECDsa replacementSigner = Signer();
        using RSA replacementRecipient = Recipient();
        Guid replacement;
        using (SecurityRegistry reopened = SecurityRegistry.Open(root))
        {
            Assert.Equal(installation, reopened.InstallationId);
            ReplacementChallenge challenge = Begin(reopened, owner, replacementSigner, replacementRecipient);
            replacement = reopened.Recovery.Complete(challenge.EnrollmentId,
                Sign(offlineRecoverySigner, challenge.Message),
                Sign(replacementSigner, challenge.Message)) ?? Guid.Empty;
            Assert.NotEqual(Guid.Empty, replacement);
            Assert.Null(reopened.Recovery.Complete(challenge.EnrollmentId,
                Sign(offlineRecoverySigner, challenge.Message),
                Sign(replacementSigner, challenge.Message)));

            Assert.Throws<UnauthorizedAccessException>(() =>
                Prove(reopened, originalSigner, lostDevice, space, SecurityPermission.Read));
            VerifiedDeviceProof newOwnerRead = Prove(reopened, replacementSigner,
                replacement, space, SecurityPermission.Read);
            Assert.Null(reopened.SpaceKeys.Download(newOwnerRead));
            OwnerRecoveryArchive? historical = reopened.SpaceKeys.DownloadOwnerRecovery(newOwnerRead);
            Assert.NotNull(historical);
            Assert.Equal(oldKey, ClientSpaceKeys.UnlockWithRecovery(installation,
                historical, contentRecovery, originalSigner, 1));
            Assert.ThrowsAny<CryptographicException>(() => ClientSpaceKeys.UnlockWithRecovery(
                installation, historical, ClientSpaceKeys.GenerateRecoverySecret(), originalSigner, 1));

            VerifiedDeviceProof ownerWrite = Prove(reopened, replacementSigner,
                replacement, space, SecurityPermission.Write);
            byte[] rotatedKey = ClientSpaceKeys.GenerateContentKey();
            SpaceKeyPublication second = ClientSpaceKeys.Protect(installation, space, 2,
                replacement, replacementSigner, reopened.SpaceKeys.Recipients(ownerWrite),
                Trusted((replacement, replacementSigner)), rotatedKey,
                ClientSpaceKeys.GenerateRecoverySecret());
            reopened.SpaceKeys.Publish(ownerWrite, second);
            AvailableSpaceKey? latest = reopened.SpaceKeys.Download(
                Prove(reopened, replacementSigner, replacement, space, SecurityPermission.Read));
            Assert.NotNull(latest);
            Assert.Equal(2, latest.Epoch);
            Assert.Equal(rotatedKey, ClientSpaceKeys.UnlockForDevice(
                installation, latest, replacementRecipient, replacementSigner, 2));
        }
    }

    [Fact]
    public void WrongRecoverySignatureConsumesChallengeAndCannotEnroll()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        using ECDsa first = Signer();
        using RSA firstRecipient = Recipient();
        using ECDsa recovery = Signer();
        using ECDsa attacker = Signer();
        using ECDsa replacement = Signer();
        using RSA replacementRecipient = Recipient();
        Guid device = Enroll(registry, owner, first, firstRecipient);
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
        Register(registry, Prove(registry, first, device, space, SecurityPermission.Write), first, recovery);

        ReplacementChallenge challenge = Begin(registry, owner, replacement, replacementRecipient);
        Assert.Null(registry.Recovery.Complete(challenge.EnrollmentId,
            Sign(attacker, challenge.Message), Sign(replacement, challenge.Message)));
        Assert.Null(registry.Recovery.Complete(challenge.EnrollmentId,
            Sign(recovery, challenge.Message), Sign(replacement, challenge.Message)));
    }

    [Fact]
    public void WrongReplacementDeviceSignatureConsumesChallenge()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        using ECDsa first = Signer();
        using RSA firstRecipient = Recipient();
        using ECDsa recovery = Signer();
        using ECDsa replacement = Signer();
        using RSA recipient = Recipient();
        using ECDsa attacker = Signer();
        Guid device = Enroll(registry, owner, first, firstRecipient);
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.TrustedHost);
        Register(registry, Prove(registry, first, device, space, SecurityPermission.Write), first, recovery);

        ReplacementChallenge challenge = Begin(registry, owner, replacement, recipient);
        Assert.Null(registry.Recovery.Complete(challenge.EnrollmentId,
            Sign(recovery, challenge.Message), Sign(attacker, challenge.Message)));
        Assert.Null(registry.Recovery.Complete(challenge.EnrollmentId,
            Sign(recovery, challenge.Message), Sign(replacement, challenge.Message)));
    }

    [Fact]
    public void HouseholdMemberCannotRegisterOwnerRecoveryOrReadOwnerArchive()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        Guid member = registry.Operator.CreatePrincipal();
        using ECDsa ownerSigner = Signer();
        using ECDsa memberSigner = Signer();
        using RSA ownerRecipient = Recipient();
        using RSA memberRecipient = Recipient();
        using ECDsa recovery = Signer();
        Guid ownerDevice = Enroll(registry, owner, ownerSigner, ownerRecipient);
        Guid memberDevice = Enroll(registry, member, memberSigner, memberRecipient);
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
        registry.Operator.Grant(owner, space, member, SecurityPermission.Read | SecurityPermission.Write);

        VerifiedDeviceProof memberWrite = Prove(registry, memberSigner, memberDevice,
            space, SecurityPermission.Write);
        byte[] key = recovery.ExportSubjectPublicKeyInfo();
        byte[] invalid = RecoveryEnrollment.RegistrationMessage(
            registry.InstallationId, member, memberDevice, 1, key);
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.Recovery.Register(memberWrite, key,
                Sign(memberSigner, invalid), Sign(recovery, invalid)));

        VerifiedDeviceProof ownerWrite = Prove(registry, ownerSigner, ownerDevice,
            space, SecurityPermission.Write);
        Register(registry, ownerWrite, ownerSigner, recovery);
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.SpaceKeys.DownloadOwnerRecovery(Prove(registry, memberSigner,
                memberDevice, space, SecurityPermission.Read)));
        Assert.Null(registry.SpaceKeys.DownloadOwnerRecovery(Prove(registry, ownerSigner,
            ownerDevice, space, SecurityPermission.Read)));

        SpaceKeyPublication pub = ClientSpaceKeys.Protect(registry.InstallationId,
            space, 1, ownerDevice, ownerSigner, registry.SpaceKeys.Recipients(ownerWrite),
            Trusted((ownerDevice, ownerSigner), (memberDevice, memberSigner)),
            ClientSpaceKeys.GenerateContentKey(), ClientSpaceKeys.GenerateRecoverySecret());
        registry.SpaceKeys.Publish(ownerWrite, pub);
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.SpaceKeys.DownloadOwnerRecovery(Prove(registry, memberSigner,
                memberDevice, space, SecurityPermission.Read)));
        Assert.NotNull(registry.SpaceKeys.DownloadOwnerRecovery(Prove(registry, ownerSigner,
            ownerDevice, space, SecurityPermission.Read)));
    }

    [Fact]
    public void RotationRequiresPriorRecoveryCredentialAndInvalidatesPendingChallenges()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        using ECDsa deviceSigner = Signer();
        using RSA deviceRecipient = Recipient();
        using ECDsa oldRecovery = Signer();
        using ECDsa newRecovery = Signer();
        using ECDsa replacement = Signer();
        using RSA replacementRecipient = Recipient();
        Guid device = Enroll(registry, owner, deviceSigner, deviceRecipient);
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.TrustedHost);
        VerifiedDeviceProof ownerProof = Prove(registry, deviceSigner, device,
            space, SecurityPermission.Write);
        Register(registry, ownerProof, deviceSigner, oldRecovery);
        ReplacementChallenge pending = Begin(registry, owner, replacement, replacementRecipient);

        byte[] newKey = newRecovery.ExportSubjectPublicKeyInfo();
        byte[] rotate = RecoveryEnrollment.RegistrationMessage(
            registry.InstallationId, owner, device, 2, newKey);
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.Recovery.Register(ownerProof, newKey,
                Sign(deviceSigner, rotate), Sign(newRecovery, rotate)));
        registry.Recovery.Register(ownerProof, newKey,
            Sign(deviceSigner, rotate), Sign(newRecovery, rotate),
            Sign(oldRecovery, rotate));

        Assert.Null(registry.Recovery.Complete(pending.EnrollmentId,
            Sign(oldRecovery, pending.Message), Sign(replacement, pending.Message)));
        ReplacementChallenge fresh = Begin(registry, owner, replacement, replacementRecipient);
        Assert.Null(registry.Recovery.Complete(fresh.EnrollmentId,
            Sign(oldRecovery, fresh.Message), Sign(replacement, fresh.Message)));
        ReplacementChallenge accepted = Begin(registry, owner, replacement, replacementRecipient);
        Assert.NotNull(registry.Recovery.Complete(accepted.EnrollmentId,
            Sign(newRecovery, accepted.Message), Sign(replacement, accepted.Message)));
    }

    [Fact]
    public void DisabledAccountCannotRecoverAndNoConfiguredCredentialReturnsNoChallenge()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        using ECDsa ownerSigner = Signer();
        using RSA recipient = Recipient();
        using ECDsa recovery = Signer();
        using ECDsa replacementSigner = Signer();
        using RSA replacementRecipient = Recipient();
        Guid device = Enroll(registry, owner, ownerSigner, recipient);
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.TrustedHost);
        Assert.Null(registry.Recovery.Begin(owner, replacementSigner.ExportSubjectPublicKeyInfo(),
            replacementRecipient.ExportSubjectPublicKeyInfo(),
            Binding(replacementSigner, replacementRecipient)));

        Register(registry, Prove(registry, ownerSigner, device, space, SecurityPermission.Write),
            ownerSigner, recovery);
        ReplacementChallenge challenge = Begin(registry, owner, replacementSigner, replacementRecipient);
        Assert.True(registry.Operator.DisablePrincipal(owner));
        Assert.Null(registry.Recovery.Complete(challenge.EnrollmentId,
            Sign(recovery, challenge.Message), Sign(replacementSigner, challenge.Message)));
        Assert.Null(registry.Recovery.Begin(owner, replacementSigner.ExportSubjectPublicKeyInfo(),
            replacementRecipient.ExportSubjectPublicKeyInfo(),
            Binding(replacementSigner, replacementRecipient)));
    }

    [Fact]
    public void ExpiredChallengeIsDeniedAndOutstandingEnrollmentIsBounded()
    {
        MutableClock clock = new(DateTimeOffset.Parse("2026-11-10T18:00:00Z"));
        using SecurityRegistry registry = SecurityRegistry.Open(root, clock);
        Guid owner = registry.Operator.CreatePrincipal();
        using ECDsa signer = Signer();
        using RSA rsa = Recipient();
        using ECDsa recovery = Signer();
        using ECDsa newSigner = Signer();
        using RSA newRecipient = Recipient();
        Guid device = Enroll(registry, owner, signer, rsa);
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
        Register(registry, Prove(registry, signer, device, space, SecurityPermission.Write), signer, recovery);
        ReplacementChallenge first = Begin(registry, owner, newSigner, newRecipient);
        for (int i = 1; i < 8; i++)
        {
            Begin(registry, owner, newSigner, newRecipient);
        }

        Assert.Throws<InvalidOperationException>(() => Begin(registry, owner, newSigner, newRecipient));
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Null(registry.Recovery.Complete(first.EnrollmentId,
            Sign(recovery, first.Message), Sign(newSigner, first.Message)));
        Assert.NotNull(Begin(registry, owner, newSigner, newRecipient));
    }

    [Fact]
    public void ReusingAlreadyEnrolledSigningKeyCannotCreateAnotherDevice()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        using ECDsa signer = Signer();
        using RSA rsa = Recipient();
        using ECDsa recovery = Signer();
        Guid device = Enroll(registry, owner, signer, rsa);
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
        Register(registry, Prove(registry, signer, device, space, SecurityPermission.Write), signer, recovery);

        ReplacementChallenge challenge = Begin(registry, owner, signer, rsa);
        Assert.Null(registry.Recovery.Complete(challenge.EnrollmentId,
            Sign(recovery, challenge.Message), Sign(signer, challenge.Message)));
        Assert.Null(registry.Recovery.Complete(challenge.EnrollmentId,
            Sign(recovery, challenge.Message), Sign(signer, challenge.Message)));
    }

    private static IReadOnlyDictionary<Guid, byte[]> Trusted(
        params (Guid DeviceId, ECDsa Signer)[] devices) =>
        devices.ToDictionary(x => x.DeviceId, x => x.Signer.ExportSubjectPublicKeyInfo());

    private static ECDsa Signer() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static RSA Recipient()
    {
        RSA rsa = RSA.Create();
        rsa.KeySize = 3072;
        return rsa;
    }

    private static byte[] Sign(ECDsa signer, byte[] message) =>
        signer.SignData(message, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    private static byte[] Binding(ECDsa signer, RSA recipient)
    {
        byte[] signing = signer.ExportSubjectPublicKeyInfo();
        byte[] wrapping = recipient.ExportSubjectPublicKeyInfo();
        return Sign(signer, DeviceWrappingKey.BindingMessage(signing, wrapping));
    }

    private static Guid Enroll(SecurityRegistry registry, Guid principal,
        ECDsa signer, RSA recipient) =>
        registry.Operator.EnrollDevice(principal, signer.ExportSubjectPublicKeyInfo(),
            recipient.ExportSubjectPublicKeyInfo(), Binding(signer, recipient));

    private static VerifiedDeviceProof Prove(SecurityRegistry registry, ECDsa signer,
        Guid deviceId, Guid space, SecurityPermission permission)
    {
        DeviceChallenge challenge = registry.DeviceProofs.IssueChallenge(deviceId,
            space, null, permission);
        return registry.DeviceProofs.Authenticate(challenge.ChallengeId,
            Sign(signer, challenge.Message)) ??
            throw new UnauthorizedAccessException("The device proof failed.");
    }

    private static void Register(SecurityRegistry registry, VerifiedDeviceProof ownerProof,
        ECDsa ownerSigner, ECDsa recoverySigner)
    {
        byte[] recoveryKey = recoverySigner.ExportSubjectPublicKeyInfo();
        byte[] message = RecoveryEnrollment.RegistrationMessage(registry.InstallationId,
            ownerProof.PrincipalId, ownerProof.DeviceId, 1, recoveryKey);
        registry.Recovery.Register(ownerProof, recoveryKey,
            Sign(ownerSigner, message), Sign(recoverySigner, message));
    }

    private static ReplacementChallenge Begin(SecurityRegistry registry, Guid principal,
        ECDsa replacementSigner, RSA replacementRecipient) =>
        registry.Recovery.Begin(principal, replacementSigner.ExportSubjectPublicKeyInfo(),
            replacementRecipient.ExportSubjectPublicKeyInfo(),
            Binding(replacementSigner, replacementRecipient)) ??
        throw new InvalidOperationException("An enrolled recovery credential is required.");

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MutableClock(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset now = current;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan interval) => now += interval;
    }
}
