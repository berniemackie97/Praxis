using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Praxis.Storage.Security;
using Praxis.Storage.Tests.Support;
using Xunit;

namespace Praxis.Storage.Tests.Security;

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
            downgrade.CommandText = "DROP TABLE trusted_epochs; PRAGMA user_version=1;";
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
