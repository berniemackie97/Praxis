using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Praxis.Storage.Security;
using Praxis.Storage.Tests.Support;
using Xunit;

namespace Praxis.Storage.Tests.Security;

public sealed class SecuritySnapshotTests : IDisposable
{
    private const long Budget = 10_000_000;
    private readonly string root = Path.Combine(TestPaths.Temp, "praxis-identity-snapshot-" + Guid.NewGuid().ToString("N"));
    private string Live => Path.Combine(root, "live");
    private string Backup => Path.Combine(root, "backup");
    private string Restored => Path.Combine(root, "restored");

    public SecuritySnapshotTests()
    {
        Directory.CreateDirectory(root);
        PrivateDirectory(root);
        Directory.CreateDirectory(Live);
        PrivateDirectory(Live);
    }

    [Fact]
    public async Task OfflineSnapshotRestoresIdentityGrantsAndEncryptedKeyEpochAfterLiveRevocation()
    {
        Guid installation, owner, member, ownerDevice, memberDevice, space, source;
        byte[] contentKey = ClientSpaceKeys.GenerateContentKey();
        byte[] recoverySecret = ClientSpaceKeys.GenerateRecoverySecret();
        using ECDsa ownerSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa memberSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using RSA ownerRecipient = RSA.Create();
        using RSA memberRecipient = RSA.Create();
        ownerRecipient.KeySize = 3072;
        memberRecipient.KeySize = 3072;

        using (SecurityRegistry live = SecurityRegistry.Open(Live))
        {
            installation = live.InstallationId;
            owner = live.Operator.CreatePrincipal();
            member = live.Operator.CreatePrincipal();
            ownerDevice = Enroll(live, owner, ownerSigner, ownerRecipient);
            memberDevice = Enroll(live, member, memberSigner, memberRecipient);
            space = live.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
            source = Guid.NewGuid();
            live.Operator.AssignSource(owner, space, source);
            live.Operator.Grant(owner, space, member, SecurityPermission.Read);
            VerifiedDeviceProof write = Prove(live, ownerSigner, ownerDevice, space, SecurityPermission.Write);
            SpaceKeyPublication publication = ClientSpaceKeys.Protect(installation, space, 1, ownerDevice,
                ownerSigner, live.SpaceKeys.Recipients(write), new Dictionary<Guid, byte[]>
                {
                    [ownerDevice] = ownerSigner.ExportSubjectPublicKeyInfo(),
                    [memberDevice] = memberSigner.ExportSubjectPublicKeyInfo()
                }, contentKey, recoverySecret);
            live.SpaceKeys.Publish(write, publication);
        }

        SecuritySnapshotReport backup = await SecurityRegistrySnapshots.BackupExistingAsync(Live, Backup, Budget);
        Assert.Equal(installation, backup.InstallationId);
        Assert.True(backup.DatabaseBytes > 0);
        Assert.Equal(backup, await SecurityRegistrySnapshots.VerifyAsync(Backup, Budget));

        using (SecurityRegistry changed = SecurityRegistry.Open(Live))
        {
            Assert.True(changed.Operator.RevokeGrant(owner, space, member));
            Assert.Throws<UnauthorizedAccessException>(() =>
                changed.DeviceProofs.IssueChallenge(memberDevice, space, source, SecurityPermission.Read));
        }

        Assert.Equal(backup, await SecurityRegistrySnapshots.RestoreAsync(Backup, Restored, Budget));
        Assert.Single(Directory.GetFileSystemEntries(Restored));
        Assert.True(File.Exists(Path.Combine(Restored, "identity.sqlite")));

        using SecurityRegistry restored = SecurityRegistry.Open(Restored);
        Assert.Equal(installation, restored.InstallationId);
        VerifiedDeviceProof ownerRead = Prove(restored, ownerSigner, ownerDevice, space, SecurityPermission.Read);
        AvailableSpaceKey? ownerKey = restored.SpaceKeys.Download(ownerRead);
        Assert.NotNull(ownerKey);
        Assert.NotNull(ownerKey.OwnerRecovery);
        Assert.Equal(contentKey, ClientSpaceKeys.UnlockForDevice(installation, ownerKey,
            ownerRecipient, ownerSigner, 1));
        Assert.Equal(contentKey, ClientSpaceKeys.UnlockWithRecovery(installation, ownerKey,
            recoverySecret, ownerSigner, 1));
        Assert.ThrowsAny<CryptographicException>(() => ClientSpaceKeys.UnlockWithRecovery(
            installation, ownerKey, ClientSpaceKeys.GenerateRecoverySecret(), ownerSigner, 1));

        VerifiedDeviceProof memberRead = Prove(restored, memberSigner, memberDevice, space,
            SecurityPermission.Read, source);
        Assert.True(restored.DeviceProofs.IsCurrent(memberRead));
        AvailableSpaceKey? memberKey = restored.SpaceKeys.Download(
            Prove(restored, memberSigner, memberDevice, space, SecurityPermission.Read));
        Assert.NotNull(memberKey);
        Assert.Null(memberKey.OwnerRecovery);
        Assert.Equal(contentKey, ClientSpaceKeys.UnlockForDevice(installation, memberKey,
            memberRecipient, ownerSigner, 1));
    }

    [Fact]
    public async Task CorruptOrPartialBackupCannotPublishARecoveredRegistry()
    {
        await MakeBackupAsync();
        Assert.Equal((await SecurityRegistrySnapshots.VerifyAsync(Backup, Budget)).DatabaseBytes,
            new FileInfo(Path.Combine(Backup, "identity.sqlite")).Length);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SecurityRegistrySnapshots.VerifyAsync(Backup, 1));
        byte[] database = await File.ReadAllBytesAsync(Path.Combine(Backup, "identity.sqlite"));
        string manifestPath = Path.Combine(Backup, "manifest.json");
        byte[] manifest = await File.ReadAllBytesAsync(manifestPath);

        await File.WriteAllBytesAsync(Path.Combine(Backup, "identity.sqlite"), [1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SecurityRegistrySnapshots.RestoreAsync(Backup, Restored, Budget));
        Assert.False(Directory.Exists(Restored));

        await File.WriteAllBytesAsync(Path.Combine(Backup, "identity.sqlite"), database);
        JsonObject edited = JsonNode.Parse(manifest)!.AsObject();
        edited["SchemaVersion"] = 999;
        await File.WriteAllTextAsync(manifestPath, edited.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SecurityRegistrySnapshots.VerifyAsync(Backup, Budget));
        await File.WriteAllBytesAsync(manifestPath, manifest);

        File.WriteAllText(Path.Combine(Backup, "extra.txt"), "unexpected");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SecurityRegistrySnapshots.VerifyAsync(Backup, Budget));
        File.Delete(Path.Combine(Backup, "extra.txt"));

        File.Delete(manifestPath);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SecurityRegistrySnapshots.RestoreAsync(Backup, Restored, Budget));
        Assert.False(Directory.Exists(Restored));
    }

    [Fact]
    public async Task CancelledOrConflictingOperationsLeaveExistingDataUnchanged()
    {
        using (SecurityRegistry registry = SecurityRegistry.Open(Live))
        {
            registry.Operator.CreatePrincipal();
        }

        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SecurityRegistrySnapshots.BackupExistingAsync(Live, Backup, Budget, cancelled.Token));
        Assert.False(Directory.Exists(Backup));

        await SecurityRegistrySnapshots.BackupExistingAsync(Live, Backup, Budget);
        Directory.CreateDirectory(Restored);
        PrivateDirectory(Restored);
        File.WriteAllText(Path.Combine(Restored, "owner.txt"), "unchanged");
        await Assert.ThrowsAsync<IOException>(() =>
            SecurityRegistrySnapshots.RestoreAsync(Backup, Restored, Budget));
        Assert.Equal("unchanged", File.ReadAllText(Path.Combine(Restored, "owner.txt")));
        Assert.Empty(Directory.GetDirectories(root, ".restored.incomplete-*"));
        Assert.Empty(Directory.GetDirectories(root, ".backup.incomplete-*"));
    }

    [Fact]
    public async Task IncompleteStagingAndExposedFilesAreNeverAcceptedAsBackups()
    {
        await MakeBackupAsync();
        string incomplete = Path.Combine(root, ".interrupted.incomplete-123");
        Directory.Move(Backup, incomplete);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SecurityRegistrySnapshots.VerifyAsync(incomplete, Budget));
        Directory.Move(incomplete, Backup);

        if (!OperatingSystem.IsWindows())
        {
            string manifest = Path.Combine(Backup, "manifest.json");
            File.SetUnixFileMode(manifest, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                SecurityRegistrySnapshots.VerifyAsync(Backup, Budget));
            File.SetUnixFileMode(manifest, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        using SecurityRegistry active = SecurityRegistry.Open(Live);
        await Assert.ThrowsAnyAsync<IOException>(() =>
            SecurityRegistrySnapshots.BackupExistingAsync(Live, Path.Combine(root, "another"), Budget));
        Assert.False(Directory.Exists(Path.Combine(root, "another")));
    }

    private async Task MakeBackupAsync()
    {
        using (SecurityRegistry registry = SecurityRegistry.Open(Live))
        {
            registry.Operator.CreatePrincipal();
        }

        await SecurityRegistrySnapshots.BackupExistingAsync(Live, Backup, Budget);
    }

    private static Guid Enroll(SecurityRegistry registry, Guid principal, ECDsa signer, RSA recipient)
    {
        byte[] signingKey = signer.ExportSubjectPublicKeyInfo();
        byte[] wrappingKey = recipient.ExportSubjectPublicKeyInfo();
        return registry.Operator.EnrollDevice(principal, signingKey, wrappingKey,
            signer.SignData(DeviceWrappingKey.BindingMessage(signingKey, wrappingKey),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    private static VerifiedDeviceProof Prove(SecurityRegistry registry, ECDsa signer, Guid device,
        Guid space, SecurityPermission permission, Guid? source = null)
    {
        DeviceChallenge challenge = registry.DeviceProofs.IssueChallenge(device, space, source, permission);
        byte[] signature = signer.SignData(challenge.Message, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return registry.DeviceProofs.Authenticate(challenge.ChallengeId, signature) ??
            throw new InvalidOperationException("The restore test device proof failed.");
    }

    private static void PrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
