using Microsoft.Data.Sqlite;
using Praxis.Infrastructure.Catalog;

namespace Praxis.Infrastructure.Processing;

public sealed record TextSegment(
    Guid SourceId, Guid ItemId, int Revision, string SourceSha256,
    int Ordinal, int Start, int Length, string? HeadingPath, string Text);

/// <summary>
/// Structure-aware segments of a revision's extracted text, stored as offsets into the text
/// derivative. Readable only for the current revision of an active source.
/// </summary>
public sealed class LocalTextSegments
{
    private readonly CatalogDatabase database;

    internal LocalTextSegments(CatalogDatabase database) => this.database = database;

    public IReadOnlyList<TextSegment> List(Guid itemId, int revision)
    {
        if (itemId == Guid.Empty || revision < 1)
        {
            throw new ArgumentException("A valid item and revision are required.");
        }

        return database.Execute(connection =>
        {
            using SqliteCommand query = connection.CreateCommand("""
                                                                 SELECT s.source_id, d.source_digest, g.ordinal, g.start_offset, g.length,
                                                                        g.heading_path, substr(d.content, g.start_offset + 1, g.length)
                                                                 FROM text_segments g
                                                                 JOIN processing_jobs j ON j.item_id = g.item_id AND j.revision = g.revision
                                                                     AND j.processor_key = g.processor_key AND j.state = 'completed'
                                                                 JOIN text_derivatives d ON d.item_id = g.item_id AND d.revision = g.revision
                                                                     AND d.processor_key = 'source-text-v1' AND d.outcome = 'completed'
                                                                 JOIN item_versions v ON v.item_id = d.item_id AND v.revision = d.revision
                                                                     AND v.digest = d.source_digest
                                                                 JOIN items i ON i.item_id = g.item_id
                                                                 JOIN sources s ON s.source_id = i.source_id
                                                                 WHERE g.item_id = $item AND g.revision = $revision AND s.status = 'active'
                                                                   AND g.revision = (SELECT MAX(latest.revision) FROM item_versions latest
                                                                                     WHERE latest.item_id = g.item_id)
                                                                 ORDER BY g.ordinal
                                                                 """);
            query.Parameters.AddWithValue("$item", itemId.ToString("D"));
            query.Parameters.AddWithValue("$revision", revision);
            using SqliteDataReader reader = query.ExecuteReader();
            List<TextSegment> segments = new List<TextSegment>();
            while (reader.Read())
            {
                segments.Add(new TextSegment(
                    Guid.Parse(reader.GetString(0)), itemId, revision, reader.GetString(1),
                    reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6)));
            }

            return segments;
        });
    }

    /// <summary>
    /// Completed segment spans of one revision for a downstream processor that already holds a
    /// verified copy of the text (see <see cref="LocalTextDerivatives.ReadLeasedInput"/>).
    /// </summary>
    internal IReadOnlyList<TextSegmentSpan> ReadCompleted(Guid itemId, int revision)
    {
        return database.Execute(connection =>
        {
            using SqliteCommand query = connection.CreateCommand("""
                                                                 SELECT g.start_offset, g.length, g.heading_path
                                                                 FROM text_segments g
                                                                 JOIN processing_jobs j ON j.item_id = g.item_id AND j.revision = g.revision
                                                                     AND j.processor_key = g.processor_key AND j.state = 'completed'
                                                                 WHERE g.item_id = $item AND g.revision = $revision
                                                                 ORDER BY g.ordinal
                                                                 """);
            query.Parameters.AddWithValue("$item", itemId.ToString("D"));
            query.Parameters.AddWithValue("$revision", revision);
            using SqliteDataReader reader = query.ExecuteReader();
            List<TextSegmentSpan> spans = new List<TextSegmentSpan>();
            while (reader.Read())
            {
                spans.Add(new TextSegmentSpan(reader.GetInt32(0), reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }

            return spans;
        });
    }

    /// <summary>Stores the segments and completes the leased job in one transaction.</summary>
    internal bool Commit(ProcessingLease lease, LeasedText input, IReadOnlyList<TextSegmentSpan> segments)
    {
        int expected = 0;
        foreach (TextSegmentSpan segment in segments)
        {
            if (segment.Start != expected || segment.Length < 1)
            {
                throw new ArgumentException("Segments must be contiguous and nonempty.", nameof(segments));
            }

            expected += segment.Length;
        }

        if (segments.Count != 0 && expected != input.Content.Length)
        {
            throw new ArgumentException("Segments must cover the extracted text exactly.", nameof(segments));
        }

        string now = CatalogValues.Timestamp(DateTimeOffset.UtcNow);
        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            if (!LocalProcessingQueue.TryCompleteLeased(connection, transaction, lease, input.Digest, now))
            {
                return false;
            }

            for (int ordinal = 0; ordinal < segments.Count; ordinal++)
            {
                using SqliteCommand insert = connection.CreateCommand("""
                                                                      INSERT INTO text_segments
                                                                          (item_id, revision, processor_key, ordinal, start_offset, length, heading_path)
                                                                      VALUES ($item, $revision, $processor, $ordinal, $start, $length, $path)
                                                                      """);
                insert.Transaction = transaction;
                ProcessingJobKey.Bind(insert, lease.ItemId, lease.Revision, lease.ProcessorKey);
                insert.Parameters.AddWithValue("$ordinal", ordinal);
                insert.Parameters.AddWithValue("$start", segments[ordinal].Start);
                insert.Parameters.AddWithValue("$length", segments[ordinal].Length);
                insert.Parameters.AddWithValue("$path", (object?)segments[ordinal].HeadingPath ?? DBNull.Value);
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
            return true;
        });
    }
}
