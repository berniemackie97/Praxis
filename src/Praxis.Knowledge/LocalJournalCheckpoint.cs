using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Praxis.Knowledge;

internal static class LocalJournalCheckpoint
{
    private const int MaxPayloadBytes = 24 * 1024 * 1024;
    private const int MaxFileBytes = 33 * 1024 * 1024;
    private const int MaxRecordBytes = 1024 * 1024;
    private const int MaxRecords = 10_000;
    private const int MaxPending = 2_000;
    private const int MaxConflicts = 2_000;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static LocalJournalState Read(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes)
        {
            throw new InvalidDataException("The local checkpoint exceeds its size limit.");
        }

        try
        {
            var container = JsonSerializer.Deserialize<CheckpointEnvelope>(
                File.ReadAllBytes(path), Options) ?? throw new InvalidDataException("The local checkpoint is empty.");
            if (container.Version != 1 || container.Payload is null ||
                container.Payload.Length > MaxPayloadBytes || container.Sha256 is null ||
                !string.Equals(
                    Convert.ToHexStringLower(SHA256.HashData(container.Payload)),
                    container.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The local checkpoint failed integrity verification.");
            }

            var result = JsonSerializer.Deserialize<LocalJournalState>(container.Payload, Options)
                ?? throw new InvalidDataException("The local checkpoint payload is empty.");
            Validate(result);
            return result;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The local checkpoint is malformed.", exception);
        }
    }

    public static void Write(string path, LocalJournalState state)
    {
        Validate(state);
        var payload = JsonSerializer.SerializeToUtf8Bytes(state, Options);
        if (payload.Length > MaxPayloadBytes)
        {
            throw new InvalidDataException("The local checkpoint exceeds its size limit.");
        }

        var envelope = new CheckpointEnvelope
        {
            Version = 1,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(payload)),
            Payload = payload
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
        if (bytes.Length > MaxFileBytes)
        {
            throw new InvalidDataException("The local checkpoint exceeds its size limit.");
        }

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static void Validate(LocalJournalState state)
    {
        if (state.CollectionEpoch == Guid.Empty || state.DeviceId == Guid.Empty ||
            state.Cursor < 0 ||
            state.Records is null || state.Pending is null ||
            state.Conflicts is null || state.Resolved is null || state.AwaitingPull is null ||
            state.Records.Count > MaxRecords || state.Pending.Count > MaxPending ||
            state.Conflicts.Count > MaxConflicts || state.Resolved.Count > MaxConflicts ||
            state.AwaitingPull.Count > MaxPending)
        {
            throw new InvalidDataException("The local checkpoint metadata is invalid.");
        }

        var ids = new HashSet<Guid>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in state.Records)
        {
            ValidateRecord(record);
            if (!ids.Add(record.Id) || !record.Deleted && !paths.Add(record.Path))
            {
                throw new InvalidDataException("The local checkpoint contains duplicate note identities or paths.");
            }
        }

        var acceptedSequences = new HashSet<long>();
        var acceptedOperations = new HashSet<Guid>();
        foreach (var accepted in state.AwaitingPull)
        {
            if (accepted is null || accepted.OperationId == Guid.Empty ||
                accepted.Sequence <= state.Cursor ||
                !acceptedSequences.Add(accepted.Sequence) ||
                !acceptedOperations.Add(accepted.OperationId))
            {
                throw new InvalidDataException("The accepted-event receipt is invalid.");
            }

            ValidateRecord(accepted.Record);
            if (!ids.Contains(accepted.Record.Id))
            {
                throw new InvalidDataException("The accepted event references a missing note.");
            }
        }

        var operations = new HashSet<Guid>();
        var lastRevision = new Dictionary<Guid, long>();
        foreach (var pending in state.Pending)
        {
            ValidateEdit(pending);
            if (!operations.Add(pending.OperationId))
            {
                throw new InvalidDataException("The local checkpoint repeats an operation identity.");
            }

            if (lastRevision.TryGetValue(pending.RecordId, out var prior) &&
                pending.ExpectedRevision != prior + 1)
            {
                throw new InvalidDataException("The local outbox has inconsistent revisions.");
            }

            lastRevision[pending.RecordId] = pending.ExpectedRevision;
        }

        var conflictIds = new HashSet<Guid>();
        foreach (var conflict in state.Conflicts)
        {
            ValidateConflict(conflict);
            if (!operations.Contains(conflict.Edit.OperationId) ||
                !conflictIds.Add(conflict.Edit.OperationId) ||
                !state.Pending.Contains(conflict.Edit))
            {
                throw new InvalidDataException("The local conflict is not associated with a pending edit.");
            }
        }

        foreach (var conflict in state.Resolved)
        {
            ValidateConflict(conflict);
            if (operations.Contains(conflict.Edit.OperationId))
            {
                throw new InvalidDataException("A resolved edit cannot remain pending.");
            }
        }
    }

    internal static void ValidateRecord(LocalNoteState? record)
    {
        if (record is null || record.Id == Guid.Empty || record.Revision <= 0 ||
            record.Markdown is null || !IsBounded(record.Markdown))
        {
            throw new InvalidDataException("The local note record is invalid.");
        }

        ValidatePath(record.Path);
    }

    internal static void ValidateEdit(PendingNoteEdit? edit)
    {
        if (edit is null || edit.OperationId == Guid.Empty || edit.RecordId == Guid.Empty ||
            edit.ExpectedRevision < 0 || !Enum.IsDefined(edit.Action))
        {
            throw new InvalidDataException("The pending edit is invalid.");
        }

        if (edit.Action == LocalNoteAction.Delete)
        {
            if (edit.Path is not null || edit.Markdown is not null)
            {
                throw new InvalidDataException("A deletion cannot contain replacement content.");
            }
        }
        else
        {
            if (edit.Markdown is null || !IsBounded(edit.Markdown))
            {
                throw new InvalidDataException("The pending edit content is invalid.");
            }

            ValidatePath(edit.Path);
        }
    }

    private static void ValidateConflict(RejectedNoteEdit? conflict)
    {
        if (conflict is null || !Enum.IsDefined(conflict.Reason))
        {
            throw new InvalidDataException("The local conflict is invalid.");
        }

        ValidateEdit(conflict.Edit);
        if (conflict.Current is not null)
        {
            ValidateRecord(conflict.Current);
            if (conflict.Current.Id != conflict.Edit.RecordId)
            {
                throw new InvalidDataException("The conflict record identity is invalid.");
            }
        }
    }

    private static void ValidatePath(string? path)
    {
        try
        {
            PortablePath.Validate(path!, markdown: true);
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("The local note path is invalid.", error);
        }
    }

    private static bool IsBounded(string markdown)
        => System.Text.Encoding.UTF8.GetByteCount(markdown) <= MaxRecordBytes;

    private sealed class CheckpointEnvelope
    {
        public CheckpointEnvelope() { }

        public int Version { get; set; }
        public string? Sha256 { get; set; }
        public byte[]? Payload { get; set; }
    }
}
