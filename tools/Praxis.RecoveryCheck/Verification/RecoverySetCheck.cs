using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Praxis.Domain.Originals;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Recovery;
using Praxis.Infrastructure.Security;

namespace Praxis.RecoveryCheck.Verification;

internal sealed record RecoverySetEvidence(CapturedOriginal Original, RecoverySetReport Snapshot,
    Guid OwnerId, Guid MemberId, Guid SpaceId, string SourcePath, DateTimeOffset CreatedUtc);

/// <summary>
/// Exercises the actual combined recovery workflow on isolated, owner-approved storage.
/// Does not contain or escrow live keys, credentials, configuration or user data.
/// </summary>
internal static class RecoverySetCheck
{
    private const long Budget = 64 * 1024 * 1024;
    private const string RequestKey = "recovery-set-physical-v1";
    private static readonly byte[] OriginalBytes = [0x50, 0x72, 0x61, 0x78, 0x69, 0x73, 0x0A];

    internal static async Task<string> CreateAsync(string scratchRoot, CancellationToken cancellationToken)
    {
        string scratch = RecoveryCheck.RequireExistingDirectory(scratchRoot);
        if (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(scratch) & ~(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)) != 0)
        {
            throw new UnauthorizedAccessException("The scratch directory must be owner-private.");
        }

        RecoveryCheck.EnsureCapacity(scratch, 512L * 1024 * 1024);
        string workspace = Path.Combine(scratch, "praxis-set-rehearsal-" + Guid.NewGuid().ToString("N"));
        RecoveryCheck.CreatePrivateDirectory(workspace);
        string liveCatalog = Path.Combine(workspace, "catalog-live");
        string liveIdentity = Path.Combine(workspace, "identity-live");
        string sourcePath = Path.Combine(workspace, "example.md");
        string backup = Path.Combine(workspace, "backup");
        string restored = Path.Combine(workspace, "restored");
        RecoveryCheck.CreatePrivateDirectory(liveCatalog);
        RecoveryCheck.CreatePrivateDirectory(liveIdentity);

        CapturedOriginal original;
        using (SqliteCatalog catalog = SqliteCatalog.Open(liveCatalog))
        using (MemoryStream bytes = new(OriginalBytes, writable: false))
        {
            original = await catalog.Originals.CaptureFileAsync(sourcePath, bytes,
                OriginalBytes.Length, RequestKey, cancellationToken);
        }

        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using RSA recipient = RSA.Create(3072);
        byte[] contentKey = ClientSpaceKeys.GenerateContentKey();
        byte[] recoverySecret = ClientSpaceKeys.GenerateRecoverySecret();
        Guid owner, member, space, device;
        Guid installation;

        using (SecurityRegistry registry = SecurityRegistry.Open(liveIdentity))
        {
            installation = registry.InstallationId;
            owner = registry.Operator.CreatePrincipal();
            member = registry.Operator.CreatePrincipal();
            byte[] signingKey = signer.ExportSubjectPublicKeyInfo();
            byte[] wrappingKey = recipient.ExportSubjectPublicKeyInfo();
            device = registry.Operator.EnrollDevice(owner, signingKey, wrappingKey,
                signer.SignData(DeviceWrappingKey.BindingMessage(signingKey, wrappingKey),
                    HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
            registry.Operator.AssignSource(owner, space, original.SourceId);
            registry.Operator.Grant(owner, space, member, SecurityPermission.Read);

            VerifiedDeviceProof proof = Prove(registry, signer, device, space, SecurityPermission.Write);
            SpaceKeyPublication publication = ClientSpaceKeys.Protect(installation, space, 1, device,
                signer, registry.SpaceKeys.Recipients(proof),
                new Dictionary<Guid, byte[]> { [device] = signingKey }, contentKey, recoverySecret);
            registry.SpaceKeys.Publish(proof, publication);
        }

        RecoverySetReport snapshot = await LocalRecoverySet.BackupExistingAsync(
            liveCatalog, liveIdentity, backup, Budget, cancellationToken);

        using (SqliteCatalog changed = SqliteCatalog.Open(liveCatalog))
        using (MemoryStream bytes = new([8, 9]))
        {
            CapturedOriginal revision = await changed.Originals.CaptureFileAsync(
                sourcePath, bytes, 2, "recovery-set-physical-v2", cancellationToken);
            Require(revision.ItemId == original.ItemId && revision.Revision == 2,
                "The post-snapshot content revision did not commit.");
        }

        using (SecurityRegistry changed = SecurityRegistry.Open(liveIdentity))
        {
            Require(changed.Operator.RevokeGrant(owner, space, member),
                "The post-snapshot permission revocation failed.");
        }

        RecoverySetReport recovered = await LocalRecoverySet.RestoreAsync(
            backup, restored, Budget, cancellationToken);
        Require(recovered == snapshot, "The restored set differs from its backup report.");
        using (SecurityRegistry registry = SecurityRegistry.Open(Path.Combine(restored, "identity")))
        {
            VerifiedDeviceProof proof = Prove(registry, signer, device, space, SecurityPermission.Read);
            AvailableSpaceKey epoch = registry.SpaceKeys.Download(proof) ??
                throw new InvalidDataException("The restored key epoch is missing.");
            Require(epoch.OwnerRecovery is not null,
                "The recovery envelope is missing from the restored registry.");
            Require(ClientSpaceKeys.UnlockWithRecovery(installation, epoch, recoverySecret, signer, 1)
                    .SequenceEqual(contentKey),
                "The independently held recovery factors cannot decrypt the restored epoch.");
            try
            {
                ClientSpaceKeys.UnlockWithRecovery(installation, epoch,
                    ClientSpaceKeys.GenerateRecoverySecret(), signer, 1);
                throw new InvalidDataException("An incorrect recovery factor decrypted the restored epoch.");
            }
            catch (CryptographicException)
            {
                Console.WriteLine("PASS failure boundary: Incorrect recovery secret");
            }
        }

        RecoverySetEvidence evidence = new(original, snapshot, owner, member, space,
            sourcePath, DateTimeOffset.UtcNow);
        await VerifyWorkspaceAsync(workspace, evidence, cancellationToken);
        await ExerciseFailureBoundariesAsync(workspace, liveCatalog, liveIdentity, cancellationToken);
        await VerifyWorkspaceAsync(workspace, evidence, cancellationToken);
        await SaveEvidenceAsync(workspace, evidence, cancellationToken);
        return workspace;
    }

    internal static async Task VerifyAsync(string workspace, CancellationToken cancellationToken)
    {
        string root = RecoveryCheck.RequireExistingDirectory(workspace);
        string path = Path.Combine(root, "evidence.json");
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(path).Length is <= 0 or > 4096)
        {
            throw new InvalidDataException("The recovery-set evidence is missing or invalid.");
        }

        await using FileStream input = File.OpenRead(path);
        RecoverySetEvidence evidence = await JsonSerializer.DeserializeAsync<RecoverySetEvidence>(
            input, cancellationToken: cancellationToken) ??
            throw new InvalidDataException("The recovery-set evidence is empty.");
        await VerifyWorkspaceAsync(root, evidence, cancellationToken);
    }

    private static async Task VerifyWorkspaceAsync(string workspace, RecoverySetEvidence evidence,
        CancellationToken cancellationToken)
    {
        RecoverySetReport verified = await LocalRecoverySet.VerifyAsync(
            Path.Combine(workspace, "backup"), Budget, cancellationToken);
        Require(verified == evidence.Snapshot &&
            verified.Identity.InstallationId != Guid.Empty &&
            evidence.Original.Length == OriginalBytes.Length &&
            evidence.Original.Sha256 == Convert.ToHexStringLower(SHA256.HashData(OriginalBytes)) &&
            evidence.Original.SourceId != Guid.Empty && evidence.Original.ItemId != Guid.Empty &&
            evidence.OwnerId != Guid.Empty && evidence.MemberId != Guid.Empty &&
            evidence.SpaceId != Guid.Empty, "The checkpoint differs from its recorded fixture.");

        string restored = Path.Combine(workspace, "restored");
        Require(Directory.Exists(restored) && !File.Exists(Path.Combine(restored, "manifest.json")) &&
            Directory.GetDirectories(restored).Length == 2,
            "The restored parent has an unexpected inventory.");
        using (SqliteCatalog catalog = SqliteCatalog.Open(Path.Combine(restored, "catalog")))
        {
            CapturedOriginal receipt = await catalog.Originals.CaptureFileAsync(evidence.SourcePath,
                Stream.Null, 0, RequestKey, cancellationToken);
            Require(receipt == evidence.Original, "The original receipt was not preserved.");
            await catalog.Originals.VerifyAsync(receipt.ItemId, cancellationToken);
            string objectPath = Path.Combine(restored, "catalog", "objects", receipt.Sha256[..2],
                receipt.Sha256[2..4], receipt.Sha256);
            Require((await File.ReadAllBytesAsync(objectPath, cancellationToken))
                .SequenceEqual(OriginalBytes), "The restored original bytes changed.");
        }

        using SecurityRegistry identity = SecurityRegistry.Open(Path.Combine(restored, "identity"));
        Require(identity.InstallationId == verified.Identity.InstallationId,
            "The restored security installation identity changed.");
        using SqliteConnection db = new(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(restored, "identity", "identity.sqlite"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        db.Open();
        using SqliteCommand query = db.CreateCommand();
        query.CommandText = """
            SELECT COUNT(*)
            FROM security_spaces s
            JOIN source_spaces src ON src.space_id=s.space_id
            JOIN space_grants g ON g.space_id=s.space_id
            JOIN space_key_epochs e ON e.space_id=s.space_id
            WHERE s.space_id=$space AND s.owner_id=$owner
              AND src.source_id=$source AND g.principal_id=$member
              AND g.permissions=1 AND g.revoked_utc IS NULL
              AND e.epoch=1
            """;
        query.Parameters.AddWithValue("$space", evidence.SpaceId.ToString("N"));
        query.Parameters.AddWithValue("$owner", evidence.OwnerId.ToString("N"));
        query.Parameters.AddWithValue("$source", evidence.Original.SourceId.ToString("N"));
        query.Parameters.AddWithValue("$member", evidence.MemberId.ToString("N"));
        Require(Convert.ToInt64(query.ExecuteScalar()) == 1,
            "Restored source grants and key-epoch metadata differ from the checkpoint.");
    }

    private static async Task ExerciseFailureBoundariesAsync(string workspace,
        string liveCatalog, string liveIdentity, CancellationToken cancellationToken)
    {
        string backup = Path.Combine(workspace, "backup");
        await ExpectFailureAsync<InvalidDataException>(() =>
            LocalRecoverySet.VerifyAsync(backup, 1, cancellationToken), "Insufficient budget");

        using (SqliteCatalog active = SqliteCatalog.Open(liveCatalog))
        {
            await ExpectFailureAsync<IOException>(() =>
                LocalRecoverySet.BackupExistingAsync(liveCatalog, liveIdentity,
                    Path.Combine(workspace, "locked-attempt"), Budget, cancellationToken),
                "Active catalog lease");
        }

        await ExpectFailureAsync<IOException>(() =>
            LocalRecoverySet.RestoreAsync(backup, Path.Combine(workspace, "restored"),
                Budget, cancellationToken), "Existing restore destination");

        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await ExpectFailureAsync<OperationCanceledException>(() =>
            LocalRecoverySet.BackupExistingAsync(liveCatalog, liveIdentity,
                Path.Combine(workspace, "cancelled"), Budget, cancelled.Token), "Cancellation");
        Require(!Directory.Exists(Path.Combine(workspace, "cancelled")),
            "Cancellation published an incomplete backup.");

        string damaged = Path.Combine(workspace, "damaged-copy");
        await LocalRecoverySet.BackupExistingAsync(liveCatalog, liveIdentity, damaged,
            Budget, cancellationToken);

        if (!OperatingSystem.IsWindows())
        {
            string manifest = Path.Combine(damaged, "manifest.json");
            File.SetUnixFileMode(manifest, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead);
            try
            {
                await ExpectFailureAsync<UnauthorizedAccessException>(() =>
                    LocalRecoverySet.VerifyAsync(damaged, Budget, cancellationToken),
                    "Exposed backup permissions");
            }
            finally
            {
                File.SetUnixFileMode(manifest, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        string identity = Path.Combine(damaged, "identity");
        string held = Path.Combine(workspace, "held-identity");
        Directory.Move(identity, held);
        try
        {
            await ExpectFailureAsync<InvalidDataException>(() =>
                LocalRecoverySet.VerifyAsync(damaged, Budget, cancellationToken),
                "Missing security registry");
        }
        finally
        {
            Directory.Move(held, identity);
        }

        await File.WriteAllBytesAsync(Path.Combine(identity, "identity.sqlite"), new byte[] { 0, 1, 2 },
            cancellationToken);
        await ExpectFailureAsync<InvalidDataException>(() =>
            LocalRecoverySet.RestoreAsync(damaged, Path.Combine(workspace, "rejected"),
                Budget, cancellationToken), "Truncated registry");
        Require(!Directory.Exists(Path.Combine(workspace, "rejected")),
            "A corrupt recovery set was published.");
    }

    private static VerifiedDeviceProof Prove(SecurityRegistry registry, ECDsa signer, Guid device,
        Guid space, SecurityPermission permission)
    {
        DeviceChallenge challenge = registry.DeviceProofs.IssueChallenge(device, space, null, permission);
        byte[] signature = signer.SignData(challenge.Message, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return registry.DeviceProofs.Authenticate(challenge.ChallengeId, signature) ??
            throw new InvalidDataException("The rehearsal device proof was rejected.");
    }

    private static async Task ExpectFailureAsync<T>(Func<Task> work, string scenario)
        where T : Exception
    {
        bool rejected = false;
        try
        {
            await work();
        }
        catch (T)
        {
            rejected = true;
        }

        Require(rejected, scenario + " was unexpectedly accepted.");
        Console.WriteLine("PASS failure boundary: " + scenario);
    }

    private static async Task SaveEvidenceAsync(string workspace, RecoverySetEvidence evidence,
        CancellationToken cancellationToken)
    {
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using FileStream output = new(Path.Combine(workspace, "evidence.json"), options);
        await JsonSerializer.SerializeAsync(output, evidence, cancellationToken: cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
