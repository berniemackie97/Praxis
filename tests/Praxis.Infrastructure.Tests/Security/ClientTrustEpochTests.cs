using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Praxis.Domain.Security;
using Praxis.Infrastructure.Persistence.Security.ClientTrust;
using Praxis.Infrastructure.Persistence.Security.Registry;
using Praxis.Infrastructure.Security.Cryptography;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Security;

public sealed class ClientTrustEpochTests : IDisposable
{
    private readonly string root = Path.Combine(TestPaths.Temp,
        "praxis-client-epoch-" + Guid.NewGuid().ToString("N"));

    public ClientTrustEpochTests()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void TwoPersistedClientsRejectOldSignedEpochAfterRevocationAndRestart()
    {
        string host = DirectoryFor("host");
        string ownerClient = DirectoryFor("owner-client");
        string memberClient = DirectoryFor("member-client");
        using ECDsa ownerSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa memberSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using RSA ownerRecipient = RSA.Create(3072);
        using RSA memberRecipient = RSA.Create(3072);
        Guid installation, ownerDevice, memberDevice, space;
        AvailableSpaceKey? oldOwnerEnvelope;
        AvailableSpaceKey? oldMemberEnvelope;
        byte[] oldKey = ClientSpaceKeys.GenerateContentKey();
        byte[] newKey = ClientSpaceKeys.GenerateContentKey();

        using (SecurityRegistry registry = SecurityRegistry.Open(host))
        {
            installation = registry.InstallationId;
            Guid owner = registry.Operator.CreatePrincipal();
            Guid member = registry.Operator.CreatePrincipal();
            ownerDevice = Enroll(registry, owner, ownerSigner, ownerRecipient);
            space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
            registry.Operator.Grant(owner, space, member, SecurityPermission.Read);
            VerifiedDeviceProof write = Prove(registry, ownerSigner, ownerDevice,
                space, SecurityPermission.Write);
            byte[] memberPublic = memberSigner.ExportSubjectPublicKeyInfo();
            byte[] memberWrapping = memberRecipient.ExportSubjectPublicKeyInfo();
            byte[] binding = Sign(memberSigner,
                DeviceWrappingKey.BindingMessage(memberPublic, memberWrapping));
            DeviceApprovalChallenge challenge = registry.Enrollment.Begin(write,
                member, memberPublic, memberWrapping, binding);
            ApprovedDeviceIdentity? receipt = registry.Enrollment.Complete(write,
                challenge.ChallengeId, Sign(ownerSigner, challenge.Message),
                Sign(memberSigner, challenge.Message));
            Assert.NotNull(receipt);
            memberDevice = receipt.DeviceId;

            using ClientDeviceTrustStore ownerPins = ClientDeviceTrustStore.Open(ownerClient,
                installation, ownerDevice, ownerSigner.ExportSubjectPublicKeyInfo());
            using ClientDeviceTrustStore memberPins = ClientDeviceTrustStore.Open(memberClient,
                installation, ownerDevice, ownerSigner.ExportSubjectPublicKeyInfo());
            ownerPins.Accept(receipt);
            memberPins.Accept(receipt);

            SpaceKeyPublication first = ClientSpaceKeys.Protect(installation, space, 1,
                ownerDevice, ownerSigner, registry.SpaceKeys.Recipients(write),
                ownerPins.TrustedRecipients(space), oldKey,
                ClientSpaceKeys.GenerateRecoverySecret());
            registry.SpaceKeys.Publish(write, first);
            oldOwnerEnvelope = registry.SpaceKeys.Download(Prove(registry, ownerSigner,
                ownerDevice, space, SecurityPermission.Read));
            oldMemberEnvelope = registry.SpaceKeys.Download(Prove(registry, memberSigner,
                memberDevice, space, SecurityPermission.Read));
            Assert.NotNull(oldOwnerEnvelope);
            Assert.NotNull(oldMemberEnvelope);
            Assert.Equal(oldKey, ClientSpaceKeys.UnlockForDevice(
                installation, oldOwnerEnvelope, ownerRecipient, ownerPins));
            Assert.Equal(oldKey, ClientSpaceKeys.UnlockForDevice(
                installation, oldMemberEnvelope, memberRecipient, memberPins));

            Assert.True(registry.Operator.RevokeGrant(owner, space, member));
            Assert.Null(registry.SpaceKeys.Download(Prove(registry, ownerSigner,
                ownerDevice, space, SecurityPermission.Read)));
            SpaceKeyPublication second = ClientSpaceKeys.Protect(installation, space, 2,
                ownerDevice, ownerSigner, registry.SpaceKeys.Recipients(write),
                ownerPins.TrustedRecipients(space), newKey,
                ClientSpaceKeys.GenerateRecoverySecret());
            registry.SpaceKeys.Publish(write, second);
            AvailableSpaceKey? latest = registry.SpaceKeys.Download(Prove(registry,
                ownerSigner, ownerDevice, space, SecurityPermission.Read));
            Assert.NotNull(latest);
            Assert.Equal(newKey, ClientSpaceKeys.UnlockForDevice(
                installation, latest, ownerRecipient, ownerPins));
            Assert.Equal(2, ownerPins.MinimumCurrentEpoch(space));
            Assert.Equal(1, memberPins.MinimumCurrentEpoch(space));
            Assert.ThrowsAny<CryptographicException>(() => ClientSpaceKeys.UnlockForDevice(
                installation, oldOwnerEnvelope, ownerRecipient, ownerPins));
        }

        using (ClientDeviceTrustStore ownerPins = ClientDeviceTrustStore.Open(ownerClient,
            installation, ownerDevice, ownerSigner.ExportSubjectPublicKeyInfo()))
        using (ClientDeviceTrustStore memberPins = ClientDeviceTrustStore.Open(memberClient,
            installation, ownerDevice, ownerSigner.ExportSubjectPublicKeyInfo()))
        using (SecurityRegistry reopened = SecurityRegistry.Open(host))
        {
            Assert.Equal(2, ownerPins.MinimumCurrentEpoch(space));
            Assert.Equal(1, memberPins.MinimumCurrentEpoch(space));
            Assert.ThrowsAny<CryptographicException>(() => ClientSpaceKeys.UnlockForDevice(
                installation, oldOwnerEnvelope!, ownerRecipient, ownerPins));
            Assert.Equal(oldKey, ClientSpaceKeys.UnlockForDevice(installation,
                oldMemberEnvelope!, memberRecipient, memberPins));
            Assert.Throws<UnauthorizedAccessException>(() =>
                Prove(reopened, memberSigner, memberDevice, space, SecurityPermission.Read));
            Assert.Equal(newKey, ClientSpaceKeys.UnlockForDevice(installation,
                reopened.SpaceKeys.Download(Prove(reopened, ownerSigner,
                    ownerDevice, space, SecurityPermission.Read))!,
                ownerRecipient, ownerPins));
        }
    }

    [Fact]
    public void LastDeviceRecoveryTransfersPersistedOwnerTrustWithoutHostEscrow()
    {
        string hostDirectory = DirectoryFor("recover-host");
        string ownerDirectory = DirectoryFor("recover-client");
        using ECDsa originalSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa recoverySigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa attackerSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa replacementSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using RSA originalRecipient = RSA.Create(3072);
        using RSA replacementRecipient = RSA.Create(3072);
        byte[] originalKey = ClientSpaceKeys.GenerateContentKey();
        byte[] newKey = ClientSpaceKeys.GenerateContentKey();
        byte[] contentRecoverySecret = ClientSpaceKeys.GenerateRecoverySecret();
        Guid installation, owner, space, originalDevice, replacementDevice;
        byte[] originalPublicKey = originalSigner.ExportSubjectPublicKeyInfo();
        byte[] recoveryPublicKey = recoverySigner.ExportSubjectPublicKeyInfo();
        byte[] replacementPublicKey = replacementSigner.ExportSubjectPublicKeyInfo();

        using (SecurityRegistry registry = SecurityRegistry.Open(hostDirectory))
        {
            installation = registry.InstallationId;
            owner = registry.Operator.CreatePrincipal();
            originalDevice = Enroll(registry, owner, originalSigner, originalRecipient);
            space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
            VerifiedDeviceProof proof = Prove(registry, originalSigner, originalDevice,
                space, SecurityPermission.Write);

            byte[] registered = RecoveryEnrollment.RegistrationMessage(installation,
                owner, originalDevice, 1, recoveryPublicKey);
            registry.Recovery.Register(proof, recoveryPublicKey,
                Sign(originalSigner, registered), Sign(recoverySigner, registered));

            using (ClientDeviceTrustStore pinned = ClientDeviceTrustStore.Open(ownerDirectory,
                installation, originalDevice, originalPublicKey))
            {
                byte[] recoveryMessage = ClientDeviceTrustStore.RecoveryAuthorityMessage(
                    installation, originalDevice, 1, recoveryPublicKey);
                pinned.RegisterRecoveryAuthority(recoveryPublicKey,
                    Sign(originalSigner, recoveryMessage),
                    Sign(recoverySigner, recoveryMessage));
                SpaceKeyPublication published = ClientSpaceKeys.Protect(installation, space, 1,
                    originalDevice, originalSigner, registry.SpaceKeys.Recipients(proof),
                    pinned.TrustedRecipients(space), originalKey, contentRecoverySecret);
                registry.SpaceKeys.Publish(proof, published);
                AvailableSpaceKey? envelope = registry.SpaceKeys.Download(
                    Prove(registry, originalSigner, originalDevice, space, SecurityPermission.Read));
                Assert.NotNull(envelope);
                Assert.Equal(originalKey, ClientSpaceKeys.UnlockForDevice(
                    installation, envelope, originalRecipient, pinned));
            }

            Assert.True(registry.Operator.RevokeDevice(originalDevice));
        }

        using (SecurityRegistry registry = SecurityRegistry.Open(hostDirectory))
        {
            byte[] applicantSigning = replacementSigner.ExportSubjectPublicKeyInfo();
            byte[] applicantWrapping = replacementRecipient.ExportSubjectPublicKeyInfo();
            byte[] binding = Sign(replacementSigner,
                DeviceWrappingKey.BindingMessage(applicantSigning, applicantWrapping));
            ReplacementChallenge pending = registry.Recovery.Begin(owner, applicantSigning,
                applicantWrapping, binding) ??
                throw new InvalidOperationException("The recovery challenge was unavailable.");
            replacementDevice = registry.Recovery.Complete(pending.EnrollmentId,
                Sign(recoverySigner, pending.Message), Sign(replacementSigner, pending.Message)) ??
                throw new InvalidOperationException("Recovery enrollment did not complete.");

            VerifiedDeviceProof read = Prove(registry, replacementSigner, replacementDevice,
                space, SecurityPermission.Read);
            OwnerRecoveryArchive? archive = registry.SpaceKeys.DownloadOwnerRecovery(read);
            Assert.NotNull(archive);
            Assert.Equal(originalKey, ClientSpaceKeys.UnlockWithRecovery(
                installation, archive, contentRecoverySecret, originalSigner, 1));

            using (ClientDeviceTrustStore stored = ClientDeviceTrustStore.Open(ownerDirectory,
                installation, originalDevice, originalPublicKey))
            {
                byte[] transfer = ClientDeviceTrustStore.OwnerTransferMessage(
                    installation, originalDevice, originalPublicKey,
                    replacementDevice, replacementPublicKey, 1);
                Assert.Throws<UnauthorizedAccessException>(() => stored.TransferOwner(
                    replacementDevice, replacementPublicKey,
                    Sign(attackerSigner, transfer), Sign(replacementSigner, transfer)));
                stored.TransferOwner(replacementDevice, replacementPublicKey,
                    Sign(recoverySigner, transfer), Sign(replacementSigner, transfer));
                Assert.Single(stored.TrustedRecipients(space));
                Assert.Contains(replacementDevice, stored.TrustedRecipients(space).Keys);
                Assert.Equal(1, stored.MinimumCurrentEpoch(space));
            }

            Assert.Throws<UnauthorizedAccessException>(() =>
                ClientDeviceTrustStore.Open(ownerDirectory, installation,
                    originalDevice, originalPublicKey));
            using ClientDeviceTrustStore recovered = ClientDeviceTrustStore.Open(ownerDirectory,
                installation, replacementDevice, replacementPublicKey);
            VerifiedDeviceProof write = Prove(registry, replacementSigner,
                replacementDevice, space, SecurityPermission.Write);
            SpaceKeyPublication rotated = ClientSpaceKeys.Protect(installation, space, 2,
                replacementDevice, replacementSigner, registry.SpaceKeys.Recipients(write),
                recovered.TrustedRecipients(space), newKey, ClientSpaceKeys.GenerateRecoverySecret());
            registry.SpaceKeys.Publish(write, rotated);
            AvailableSpaceKey? newOffer = registry.SpaceKeys.Download(Prove(registry,
                replacementSigner, replacementDevice, space, SecurityPermission.Read));
            Assert.NotNull(newOffer);
            Assert.Equal(newKey, ClientSpaceKeys.UnlockForDevice(installation, newOffer,
                replacementRecipient, recovered));
            Assert.Equal(2, recovered.MinimumCurrentEpoch(space));
        }
    }

    [Fact]
    public void LegacyClientTrustDatabaseUpgradesWithoutReplacingPinnedIdentity()
    {
        string directory = DirectoryFor("client");
        Guid installation = Guid.NewGuid(), ownerDevice = Guid.NewGuid(), space = Guid.NewGuid();
        using ECDsa ownerSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] ownerPublic = ownerSigner.ExportSubjectPublicKeyInfo();

        using (ClientDeviceTrustStore first = ClientDeviceTrustStore.Open(directory,
            installation, ownerDevice, ownerPublic))
        {
            Assert.Equal(1, first.MinimumCurrentEpoch(space));
        }

        using (SqliteConnection legacy = new(
            $"Data Source={Path.Combine(directory, "trust.sqlite")};Pooling=False"))
        {
            legacy.Open();
            using SqliteCommand downgrade = legacy.CreateCommand();
            downgrade.CommandText = "DROP TABLE trusted_epochs; DROP TABLE recovery_authority; PRAGMA user_version=1;";
            downgrade.ExecuteNonQuery();
        }

        using (ClientDeviceTrustStore upgraded = ClientDeviceTrustStore.Open(directory,
            installation, ownerDevice, ownerPublic))
        {
            Assert.Equal(1, upgraded.MinimumCurrentEpoch(space));
            Assert.Single(upgraded.TrustedRecipients(space));
        }

        using SqliteConnection inspected = new(
            $"Data Source={Path.Combine(directory, "trust.sqlite")};Pooling=False");
        inspected.Open();
        using SqliteCommand version = inspected.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal(2L, Convert.ToInt64(version.ExecuteScalar()));
    }

    private string DirectoryFor(string name)
    {
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static Guid Enroll(SecurityRegistry registry, Guid principal, ECDsa signing, RSA wrapping)
    {
        byte[] publicSigning = signing.ExportSubjectPublicKeyInfo();
        byte[] publicWrapping = wrapping.ExportSubjectPublicKeyInfo();
        return registry.Operator.EnrollDevice(principal, publicSigning, publicWrapping,
            Sign(signing, DeviceWrappingKey.BindingMessage(publicSigning, publicWrapping)));
    }

    private static VerifiedDeviceProof Prove(SecurityRegistry registry, ECDsa signing,
        Guid device, Guid space, SecurityPermission permission)
    {
        DeviceChallenge challenge = registry.DeviceProofs.IssueChallenge(device, space, null, permission);
        return registry.DeviceProofs.Authenticate(challenge.ChallengeId,
            Sign(signing, challenge.Message)) ??
            throw new UnauthorizedAccessException("The device proof was not authorized.");
    }

    private static byte[] Sign(ECDsa signing, byte[] message) =>
        signing.SignData(message, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
