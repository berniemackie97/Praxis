using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Praxis.Notes.Journal;
using Praxis.Infrastructure.Security.Cryptography;

namespace Praxis.Infrastructure.Persistence.Sync;

internal sealed class SealedNoteOutbox
{
    private const int MaximumFileBytes = 128 * 1024;
    private readonly string root;
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private const string ContextFileName = "outbox-context.json";

    public SealedNoteOutbox(string directory, Guid spaceId, Guid collectionEpoch,
        Guid deviceId, long contentEpoch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory) || spaceId == Guid.Empty ||
            collectionEpoch == Guid.Empty || deviceId == Guid.Empty || contentEpoch < 1)
        {
            throw new ArgumentException("The encrypted outbox needs an absolute private path and identities.");
        }

        root = Path.GetFullPath(directory);
        if (!Directory.Exists(root) ||
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The encrypted outbox requires an existing regular directory.");
        }

        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode publicPermissions = UnixFileMode.GroupRead |
                UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(root) & publicPermissions) != 0)
            {
                throw new UnauthorizedAccessException("The encrypted outbox must be owner-private.");
            }
        }

        string contextPath = Path.Combine(root, ContextFileName);
        if (!File.Exists(contextPath) &&
            Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new InvalidDataException(
                "A new encrypted outbox must start in a dedicated empty private directory.");
        }

        OutboxContext expected = new(1, spaceId, collectionEpoch, deviceId, contentEpoch);
        if (File.Exists(contextPath))
        {
            VerifyRegularPrivateFile(contextPath);
            if (new FileInfo(contextPath).Length > 1024)
            {
                throw new InvalidDataException("The encrypted outbox context is invalid.");
            }

            OutboxContext actual;
            try
            {
                actual = JsonSerializer.Deserialize<OutboxContext>(File.ReadAllBytes(contextPath), Json)
                    ?? throw new InvalidDataException("The encrypted outbox context is missing.");
            }
            catch (JsonException error)
            {
                throw new InvalidDataException("The encrypted outbox context is malformed.", error);
            }

            if (actual != expected)
            {
                throw new InvalidDataException("This encrypted outbox belongs to another device or key epoch.");
            }
        }
        else
        {
            WriteNew(contextPath, JsonSerializer.SerializeToUtf8Bytes(expected, Json));
        }
    }

    public OpaqueSyncMutation GetOrSeal(PendingNoteEdit edit, Guid space,
        Guid collectionEpoch, Guid device, long contentEpoch, byte[] key,
        IReadOnlyList<LocalNoteState> records)
    {
        string path = Path.Combine(root, edit.OperationId.ToString("N") + ".json");
        OpaqueSyncAction action = edit.Action switch
        {
            LocalNoteAction.Upsert => OpaqueSyncAction.Upsert,
            LocalNoteAction.Delete => OpaqueSyncAction.Delete,
            LocalNoteAction.Restore => OpaqueSyncAction.Restore,
            _ => throw new InvalidDataException("The queued note action is unsupported.")
        };
        LocalNoteState? old = records.FirstOrDefault(note => note.Id == edit.RecordId);
        OpaqueNoteContent note = edit.Action == LocalNoteAction.Delete
            ? old is { Deleted: false } && old.Revision == edit.ExpectedRevision
                ? new OpaqueNoteContent(old.Path, old.Markdown)
                : throw new InvalidDataException("A deletion has no matching local predecessor.")
            : new OpaqueNoteContent(edit.Path!, edit.Markdown!);

        OpaqueSyncMutation expected = new(1, space, collectionEpoch, device,
            edit.OperationId, edit.RecordId, edit.ExpectedRevision, action,
            contentEpoch, []);
        if (File.Exists(path))
        {
            VerifyRegularPrivateFile(path);
            if (new FileInfo(path).Length > MaximumFileBytes)
            {
                throw new InvalidDataException("The encrypted outbox entry exceeds its size limit.");
            }

            OpaqueSyncMutation stored;
            try
            {
                stored = JsonSerializer.Deserialize<OpaqueSyncMutation>(File.ReadAllBytes(path), Json)
                    ?? throw new InvalidDataException("The encrypted outbox entry is empty.");
            }
            catch (JsonException error)
            {
                throw new InvalidDataException("The encrypted outbox entry is malformed.", error);
            }

            if (stored.ProtocolVersion != expected.ProtocolVersion ||
                stored.SpaceId != expected.SpaceId || stored.CollectionEpoch != expected.CollectionEpoch ||
                stored.DeviceId != expected.DeviceId || stored.OperationId != expected.OperationId ||
                stored.RecordId != expected.RecordId ||
                stored.ExpectedRevision != expected.ExpectedRevision ||
                stored.Action != expected.Action || stored.ContentEpoch != expected.ContentEpoch ||
                stored.Ciphertext is null)
            {
                throw new InvalidDataException("The sealed operation does not match the current authorized context.");
            }

            OpaqueNoteContent decoded = ClientOpaqueNoteCipher.Decrypt(key, space,
                collectionEpoch, edit.RecordId, checked(edit.ExpectedRevision + 1),
                action, contentEpoch, stored.Ciphertext);
            if (decoded != note)
            {
                throw new InvalidDataException("The sealed operation does not match the pending edit.");
            }

            return stored;
        }

        byte[] ciphertext = ClientOpaqueNoteCipher.Encrypt(key, space,
            collectionEpoch, edit.RecordId, checked(edit.ExpectedRevision + 1),
            action, contentEpoch, note);
        OpaqueSyncMutation mutation = expected with { Ciphertext = ciphertext };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(mutation, Json);
        if (bytes.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("The encrypted outbox entry exceeds its size limit.");
        }

        WriteNew(path, bytes);

        return mutation;
    }

    public void Prune(IReadOnlyList<PendingNoteEdit> pending)
    {
        HashSet<string> retained = pending
            .Select(edit => edit.OperationId.ToString("N") + ".json")
            .ToHashSet(StringComparer.Ordinal);
        retained.Add(ContextFileName);
        string[] entries = Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly).ToArray();
        foreach (string path in entries)
        {
            string name = Path.GetFileName(path);
            if (name != ContextFileName &&
                !Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _))
            {
                throw new InvalidDataException("The encrypted outbox contains an unrelated JSON file.");
            }

            VerifyRegularPrivateFile(path);
        }

        foreach (string path in entries)
        {
            if (!retained.Contains(Path.GetFileName(path)))
            {
                File.Delete(path);
            }
        }
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        string staging = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
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

            using (FileStream output = new(staging, options))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }

            File.Move(staging, path);
        }
        finally
        {
            File.Delete(staging);
        }
    }

    private sealed record OutboxContext(int Version, Guid SpaceId, Guid CollectionEpoch,
        Guid DeviceId, long ContentEpoch);

    private static void VerifyRegularPrivateFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("An encrypted outbox entry is a filesystem link.");
        }

        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode publicPermissions = UnixFileMode.GroupRead |
                UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(path) & publicPermissions) != 0)
            {
                throw new UnauthorizedAccessException("An encrypted outbox entry is not private.");
            }
        }
    }
}
