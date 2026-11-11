using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Praxis.Domain.Originals;
using Praxis.Domain.Security;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Persistence.Security.Registry;
using Praxis.Infrastructure.Recovery;
using Praxis.Infrastructure.Security.Cryptography;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Recovery;

public sealed class LocalRecoverySetTests : IDisposable
{
    private const long Budget = 20_000_000;
    private readonly string root = Path.Combine(TestPaths.Temp, "praxis-recovery-set-" + Guid.NewGuid().ToString("N"));
    private string Catalog => Path.Combine(root, "catalog-live");
    private string Identity => Path.Combine(root, "identity-live");
    private string Backup => Path.Combine(root, "complete-set");
    private string Restored => Path.Combine(root, "restored-set");
    private string Source => Path.Combine(root, "submitted", "note.md");

    public LocalRecoverySetTests()
    {
        Directory.CreateDirectory(root);
        Private(root);
        Directory.CreateDirectory(Catalog);
        Private(Catalog);
        Directory.CreateDirectory(Identity);
        Private(Identity);
    }

    [Fact]
    public async Task QuiescentSetPreservesOriginalsGrantsAndEncryptedKeyEpochBeforeLaterMutations()
    {
        CapturedOriginal original = await CreateSourceAsync();
        using ECDsa ownerSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa memberSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using RSA ownerRecipient = RSA.Create(3072);
        using RSA memberRecipient = RSA.Create(3072);
        byte[] contentKey = ClientSpaceKeys.GenerateContentKey();
        byte[] recoverySecret = ClientSpaceKeys.GenerateRecoverySecret();
        Guid installation, owner, member, ownerDevice, memberDevice, space;
        using (SecurityRegistry identity = SecurityRegistry.Open(Identity))
        {
            installation = identity.InstallationId;
            owner = identity.Operator.CreatePrincipal();
            member = identity.Operator.CreatePrincipal();
            ownerDevice = Enroll(identity, owner, ownerSigner, ownerRecipient);
            memberDevice = Enroll(identity, member, memberSigner, memberRecipient);
            space = identity.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
            identity.Operator.AssignSource(owner, space, original.SourceId);
            identity.Operator.Grant(owner, space, member, SecurityPermission.Read);
            VerifiedDeviceProof write = Prove(identity, ownerSigner, ownerDevice, space, SecurityPermission.Write);
            SpaceKeyPublication epoch = ClientSpaceKeys.Protect(
                installation, space, 1, ownerDevice, ownerSigner, identity.SpaceKeys.Recipients(write),
                new Dictionary<Guid, byte[]>
                {
                    [ownerDevice] = ownerSigner.ExportSubjectPublicKeyInfo(),
                    [memberDevice] = memberSigner.ExportSubjectPublicKeyInfo()
                }, contentKey, recoverySecret);
            identity.SpaceKeys.Publish(write, epoch);
        }

        RecoverySetReport checkpoint = await RecoverySet.BackupExistingAsync(
            Catalog, Identity, Backup, Budget);
        Assert.Equal(installation, checkpoint.Identity.InstallationId);
        Assert.Equal(1, checkpoint.Catalog.ObjectCount);
        Assert.Equal(checkpoint, await RecoverySet.VerifyAsync(Backup, Budget));

        using (SqliteCatalog later = SqliteCatalog.Open(Catalog))
        {
            using MemoryStream changed = new([9, 8, 7]);
            CapturedOriginal revision = await later.Originals.CaptureFileAsync(Source, changed, 3, "later");
            Assert.Equal(2, revision.Revision);
        }

        using (SecurityRegistry later = SecurityRegistry.Open(Identity))
        {
            Assert.True(later.Operator.RevokeGrant(owner, space, member));
        }

        Assert.Equal(checkpoint, await RecoverySet.RestoreAsync(Backup, Restored, Budget));
        Assert.Equal(new[] { "catalog", "identity" },
            Directory.GetDirectories(Restored).Select(Path.GetFileName).Order().ToArray());
        Assert.Empty(Directory.GetFiles(Restored));

        using (SqliteCatalog catalog = SqliteCatalog.Open(Path.Combine(Restored, "catalog")))
        {
            CapturedOriginal recovered = await catalog.Originals.CaptureFileAsync(
                Source, Stream.Null, 0, "original");
            Assert.Equal(original, recovered);
            await catalog.Originals.VerifyAsync(recovered.ItemId);
            Assert.Equal(new byte[] { 1, 2, 3 },
                await File.ReadAllBytesAsync(ObjectPath(Path.Combine(Restored, "catalog"), original.Sha256)));
            Assert.Equal(new OriginalRecoveryInspection(0, 1, 1, 0, 0, 0),
                await catalog.Recovery.InspectAsync());
        }

        using (SecurityRegistry identity = SecurityRegistry.Open(Path.Combine(Restored, "identity")))
        {
            Assert.Equal(installation, identity.InstallationId);
            VerifiedDeviceProof memberRead = Prove(identity, memberSigner, memberDevice, space,
                SecurityPermission.Read, original.SourceId);
            Assert.True(identity.DeviceProofs.IsCurrent(memberRead));
            AvailableSpaceKey? ownerKey = identity.SpaceKeys.Download(
                Prove(identity, ownerSigner, ownerDevice, space, SecurityPermission.Read));
            Assert.NotNull(ownerKey);
            Assert.Equal(contentKey, ClientSpaceKeys.UnlockWithRecovery(
                installation, ownerKey, recoverySecret, ownerSigner, 1));
            Assert.ThrowsAny<CryptographicException>(() => ClientSpaceKeys.UnlockWithRecovery(
                installation, ownerKey, ClientSpaceKeys.GenerateRecoverySecret(), ownerSigner, 1));
            AvailableSpaceKey? memberKey = identity.SpaceKeys.Download(
                Prove(identity, memberSigner, memberDevice, space, SecurityPermission.Read));
            Assert.NotNull(memberKey);
            Assert.Null(memberKey.OwnerRecovery);
            Assert.Equal(contentKey, ClientSpaceKeys.UnlockForDevice(
                installation, memberKey, memberRecipient, ownerSigner, 1));
        }
    }

    [Fact]
    public async Task LegacyCatalogSchemaIsPreservedInBackupAndUpgradedOnlyDuringRestore()
    {
        string databasePath = Path.Combine(Catalog, "catalog.sqlite");
        using (SqliteConnection database = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString()))
        {
            database.Open();
            foreach (string name in new[] { "Catalog.V1.sql", "Catalog.V2.sql" })
            {
                using Stream schema = typeof(SqliteCatalog).Assembly
                    .GetManifestResourceStream("Praxis.Infrastructure." + name)!;
                using StreamReader reader = new(schema);
                using SqliteCommand migration = database.CreateCommand();
                migration.CommandText = await reader.ReadToEndAsync();
                migration.ExecuteNonQuery();
            }
        }

        using (SecurityRegistry identity = SecurityRegistry.Open(Identity))
        {
            identity.Operator.CreatePrincipal();
        }

        await RecoverySet.BackupExistingAsync(Catalog, Identity, Backup, Budget);
        Assert.Equal(2L, ReadSchemaVersion(databasePath));
        Assert.Equal(2L, ReadSchemaVersion(Path.Combine(Backup, "catalog", "catalog.sqlite")));
        RecoverySetReport verified = await RecoverySet.VerifyAsync(Backup, Budget);
        Assert.Equal(0, verified.Catalog.ObjectCount);
        await RecoverySet.RestoreAsync(Backup, Restored, Budget);
        using SqliteCatalog restored = SqliteCatalog.Open(Path.Combine(Restored, "catalog"));
        Assert.True(restored.SchemaVersion > 2);
        Assert.Equal(2L, ReadSchemaVersion(databasePath));
    }

    [Fact]
    public async Task DamagedIncompleteOrUnexpectedComponentCannotProduceRestoredSet()
    {
        await CreateBasicSetAsync();
        RecoverySetReport verified = await RecoverySet.VerifyAsync(Backup, Budget);
        Assert.True(verified.Catalog.TotalBytes > 0);

        string identityPath = Path.Combine(Backup, "identity", "identity.sqlite");
        byte[] saved = await File.ReadAllBytesAsync(identityPath);
        await File.WriteAllBytesAsync(identityPath, [1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RecoverySet.RestoreAsync(Backup, Restored, Budget));
        Assert.False(Directory.Exists(Restored));
        await File.WriteAllBytesAsync(identityPath, saved);

        string catalogObject = Directory.GetFiles(Path.Combine(Backup, "catalog", "objects"),
            "*", SearchOption.AllDirectories).Single();
        byte[] original = await File.ReadAllBytesAsync(catalogObject);
        await File.WriteAllBytesAsync(catalogObject, [0, 0, 0]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RecoverySet.VerifyAsync(Backup, Budget));
        await File.WriteAllBytesAsync(catalogObject, original);

        string manifest = Path.Combine(Backup, "manifest.json");
        byte[] originalManifest = await File.ReadAllBytesAsync(manifest);
        JsonObject invalid = JsonNode.Parse(originalManifest)!.AsObject();
        invalid["FormatVersion"] = 999;
        await File.WriteAllTextAsync(manifest, invalid.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RecoverySet.RestoreAsync(Backup, Restored, Budget));
        invalid["FormatVersion"] = 1;
        invalid["Identity"]!.AsObject()["DatabaseSha256"] = new string('0', 64);
        await File.WriteAllTextAsync(manifest, invalid.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RecoverySet.VerifyAsync(Backup, Budget));
        await File.WriteAllBytesAsync(manifest, originalManifest);

        File.WriteAllText(Path.Combine(Backup, "extra"), "unexpected");
        await Assert.ThrowsAsync<InvalidDataException>(() => RecoverySet.VerifyAsync(Backup, Budget));
        File.Delete(Path.Combine(Backup, "extra"));

        string incomplete = Path.Combine(root, ".unfinished.incomplete-123");
        Directory.Move(Backup, incomplete);
        await Assert.ThrowsAsync<InvalidDataException>(() => RecoverySet.VerifyAsync(incomplete, Budget));
        Directory.Move(incomplete, Backup);
        Assert.Equal(verified, await RecoverySet.VerifyAsync(Backup, Budget));
        Assert.False(Directory.Exists(Restored));
    }

    [Fact]
    public async Task LockedSourcesCancellationAndExistingTargetsDoNotPublishBackupOrRestore()
    {
        await CreateSourceAsync();
        using (SqliteCatalog active = SqliteCatalog.Open(Catalog))
        {
            using SecurityRegistry identity = SecurityRegistry.Open(Identity);
            await Assert.ThrowsAnyAsync<IOException>(() =>
                RecoverySet.BackupExistingAsync(Catalog, Identity, Backup, Budget));
        }

        using (SecurityRegistry active = SecurityRegistry.Open(Identity))
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                RecoverySet.BackupExistingAsync(Catalog, Identity, Backup, Budget));
        }

        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RecoverySet.BackupExistingAsync(Catalog, Identity, Backup, Budget, cancelled.Token));
        Assert.False(Directory.Exists(Backup));

        await Assert.ThrowsAsync<InvalidOperationException>(() => RecoverySet.BackupExistingAsync(
            Catalog, Identity, Path.Combine(Catalog, "nested"), Budget));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RecoverySet.BackupExistingAsync(
            Catalog, Identity, Path.Combine(Identity, "nested"), Budget));

        await RecoverySet.BackupExistingAsync(Catalog, Identity, Backup, Budget);
        Directory.CreateDirectory(Restored);
        Private(Restored);
        File.WriteAllText(Path.Combine(Restored, "preserve.txt"), "unchanged");
        await Assert.ThrowsAsync<IOException>(() => RecoverySet.RestoreAsync(Backup, Restored, Budget));
        Assert.Equal("unchanged", File.ReadAllText(Path.Combine(Restored, "preserve.txt")));
        Assert.Empty(Directory.GetDirectories(root, ".restored-set.incomplete-*"));
    }

    [Fact]
    public async Task CorruptPermissionsBudgetOrMissingComponentCannotBeAccepted()
    {
        await CreateBasicSetAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => RecoverySet.VerifyAsync(Backup, 1));
        if (!OperatingSystem.IsWindows())
        {
            string manifest = Path.Combine(Backup, "manifest.json");
            File.SetUnixFileMode(manifest, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                RecoverySet.VerifyAsync(Backup, Budget));
            File.SetUnixFileMode(manifest, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        string missing = Path.Combine(Backup, "identity");
        Directory.Move(missing, Path.Combine(root, "held-identity"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RecoverySet.RestoreAsync(Backup, Restored, Budget));
        Assert.False(Directory.Exists(Restored));
    }

    private async Task<CapturedOriginal> CreateSourceAsync()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(Catalog);
        using MemoryStream bytes = new([1, 2, 3]);
        return await catalog.Originals.CaptureFileAsync(Source, bytes, 3, "original");
    }

    private async Task CreateBasicSetAsync()
    {
        CapturedOriginal original = await CreateSourceAsync();
        using (SecurityRegistry registry = SecurityRegistry.Open(Identity))
        {
            Guid owner = registry.Operator.CreatePrincipal();
            Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.TrustedHost);
            registry.Operator.AssignSource(owner, space, original.SourceId);
        }

        await RecoverySet.BackupExistingAsync(Catalog, Identity, Backup, Budget);
    }

    private static long ReadSchemaVersion(string databasePath)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string ObjectPath(string directory, string digest) =>
        Path.Combine(directory, "objects", digest[..2], digest[2..4], digest);

    private static Guid Enroll(SecurityRegistry registry, Guid principal, ECDsa signer, RSA recipient)
    {
        byte[] signing = signer.ExportSubjectPublicKeyInfo();
        byte[] wrapping = recipient.ExportSubjectPublicKeyInfo();
        return registry.Operator.EnrollDevice(principal, signing, wrapping, signer.SignData(
            DeviceWrappingKey.BindingMessage(signing, wrapping), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    private static VerifiedDeviceProof Prove(SecurityRegistry registry, ECDsa signer, Guid device,
        Guid space, SecurityPermission permission, Guid? source = null)
    {
        DeviceChallenge challenge = registry.DeviceProofs.IssueChallenge(device, space, source, permission);
        byte[] signature = signer.SignData(challenge.Message, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return registry.DeviceProofs.Authenticate(challenge.ChallengeId, signature) ??
            throw new InvalidOperationException("The recovered security proof was rejected.");
    }

    private static void Private(string path)
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
