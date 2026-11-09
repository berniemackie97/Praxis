using System.Text;

namespace Praxis.Storage;

internal sealed record BudgetSource(
    Guid SourceId, Guid ItemId, int Revision, string Sha256, string Content);

public sealed partial class LocalCatalog
{
    internal async Task<BudgetSource> ReadBudgetSourceAsync(
        Guid sourceId, CancellationToken cancellationToken)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("Choose a captured financial CSV source.", nameof(sourceId));
        }

        (Guid ItemId, int Revision, string Digest, long Length, string Locator) source;
        lock (connection)
        {
            source = CurrentBudgetSource(sourceId)
                ?? throw new InvalidOperationException("The selected source is not active.");
        }

        if (!Path.GetExtension(source.Locator).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Budget analysis requires a selected CSV original.", nameof(sourceId));
        }

        var bytes = await new OriginalFileStore(Path.GetDirectoryName(DatabasePath)!)
            .ReadVerifiedBytesAsync(new OriginalObject(source.Digest, source.Length),
                LocalMonthlyBudgetAnalyzer.MaximumBytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        string content;
        try
        {
            var offset = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)
                ? Encoding.UTF8.Preamble.Length : 0;
            content = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The financial CSV is not valid UTF-8.", exception);
        }

        if (content.Any(c => (c < 0x20 && c is not ('\n' or '\r' or '\t')) || c == 0x7f))
        {
            throw new InvalidDataException("The selected financial CSV contains control characters.");
        }

        lock (connection)
        {
            if (CurrentBudgetSource(sourceId) != source)
            {
                throw new InvalidOperationException("The selected financial source changed during analysis.");
            }
        }

        return new BudgetSource(sourceId, source.ItemId, source.Revision, source.Digest, content);
    }

    internal void AssertBudgetSourceCurrent(
        Guid sourceId, Guid itemId, int revision, string sha256)
    {
        lock (connection)
        {
            var current = CurrentBudgetSource(sourceId);
            if (current is null || current.Value.ItemId != itemId ||
                current.Value.Revision != revision ||
                current.Value.Digest != sha256)
            {
                throw new InvalidOperationException("The financial source changed or was revoked.");
            }
        }
    }

    private (Guid ItemId, int Revision, string Digest, long Length, string Locator)?
        CurrentBudgetSource(Guid sourceId)
    {
        using var query = Command(connection, """
            SELECT i.item_id, v.revision, v.digest, o.byte_length, s.locator
            FROM sources s
            JOIN items i ON i.source_id = s.source_id
            JOIN item_versions v ON v.item_id = i.item_id
            JOIN original_objects o ON o.digest = v.digest
            WHERE s.source_id = $source AND s.status = 'active' AND s.source_kind = 'file'
              AND v.revision = (SELECT MAX(latest.revision)
                                FROM item_versions latest WHERE latest.item_id = v.item_id)
            LIMIT 1
            """);
        query.Parameters.AddWithValue("$source", sourceId.ToString("D"));
        using var reader = query.ExecuteReader();
        return reader.Read()
            ? (Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetString(4))
            : null;
    }
}
