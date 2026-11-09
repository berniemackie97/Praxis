using System.Globalization;
using System.Text;

namespace Praxis.Storage;

public sealed record LocalSearchHit(
    Guid SourceId, Guid ItemId, int Revision, string Title,
    string SourceLocator, string OriginalSha256, string Snippet);

public sealed partial class LocalCatalog
{
    public IReadOnlyList<LocalSearchHit> SearchText(string query, int limit = 20)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var expression = BuildTextQuery(query);
        lock (connection)
        {
            using var command = Command(connection, """
                SELECT s.source_id, v.item_id, v.revision, s.locator, v.digest,
                       snippet(text_search, 4, '⟦', '⟧', '…', 24)
                FROM text_search
                JOIN item_versions v
                    ON v.item_id = text_search.item_id
                    AND v.revision = CAST(text_search.revision AS INTEGER)
                JOIN items i ON i.item_id = v.item_id
                JOIN sources s
                    ON s.source_id = i.source_id AND s.source_id = text_search.source_id
                JOIN text_derivatives d
                    ON d.item_id = v.item_id AND d.revision = v.revision
                    AND d.source_digest = v.digest AND d.outcome = 'completed'
                    AND d.processor_key = 'source-text-v1'
                JOIN processing_jobs j
                    ON j.item_id = d.item_id AND j.revision = d.revision
                    AND j.processor_key = d.processor_key AND j.state = 'completed'
                WHERE text_search MATCH $query
                  AND s.status = 'active'
                  AND v.revision = (
                      SELECT MAX(latest.revision) FROM item_versions latest
                      WHERE latest.item_id = v.item_id)
                ORDER BY bm25(text_search), s.locator COLLATE BINARY,
                         v.item_id, v.revision
                LIMIT $limit
                """);
            command.CommandTimeout = 5;
            command.Parameters.AddWithValue("$query", expression);
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var hits = new List<LocalSearchHit>();
            while (reader.Read())
            {
                var locator = reader.GetString(3);
                var snippet = reader.IsDBNull(5) ? "" : reader.GetString(5);
                if (snippet.Length > 480)
                {
                    snippet = snippet[..480];
                }

                hits.Add(new LocalSearchHit(
                    Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                    Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                    Path.GetFileName(locator), locator, reader.GetString(4), snippet));
            }

            return hits;
        }
    }

    public int RebuildTextSearchIndex(
        int maxDocuments = 100_000, long maxTextBytes = 512L * 1024 * 1024)
    {
        if (maxDocuments is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDocuments));
        }

        if (maxTextBytes is < 1 or > 512L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTextBytes));
        }

        lock (connection)
        {
            using var transaction = connection.BeginTransaction();
            using var count = Command(connection, """
                SELECT COUNT(*), COALESCE(SUM(length(CAST(content AS BLOB))), 0)
                FROM eligible_text_search
                """);
            count.Transaction = transaction;
            count.CommandTimeout = 30;
            int eligible;
            using (var reader = count.ExecuteReader())
            {
                reader.Read();
                eligible = reader.GetInt32(0);
                if (eligible > maxDocuments || reader.GetInt64(1) > maxTextBytes)
                {
                    throw new InvalidOperationException("The rebuild exceeds its data budget.");
                }
            }

            using var clear = Command(connection, "DELETE FROM text_search");
            clear.Transaction = transaction;
            clear.CommandTimeout = 30;
            clear.ExecuteNonQuery();

            using var populate = Command(connection, """
                INSERT INTO text_search (item_id, revision, source_id, locator, content)
                SELECT item_id, revision, source_id, locator, content
                FROM eligible_text_search ORDER BY item_id, revision
                """);
            populate.Transaction = transaction;
            populate.CommandTimeout = 30;
            populate.ExecuteNonQuery();
            transaction.Commit();
            return eligible;
        }
    }

    private static string BuildTextQuery(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Length > 256 || query.Any(char.IsControl))
        {
            throw new ArgumentException("Search queries must be bounded printable text.", nameof(query));
        }

        var terms = new List<string>();
        var token = new StringBuilder();
        foreach (var character in query)
        {
            if (char.IsLetterOrDigit(character))
            {
                token.Append(character);
                if (token.Length > 64)
                {
                    throw new ArgumentException("Search terms cannot exceed 64 characters.", nameof(query));
                }
            }
            else if (token.Length > 0)
            {
                terms.Add(token.ToString());
                token.Clear();
            }
        }

        if (token.Length > 0)
        {
            terms.Add(token.ToString());
        }

        if (terms.Count is < 1 or > 16)
        {
            throw new ArgumentException("Search queries must contain one to sixteen terms.", nameof(query));
        }

        return string.Join(" AND ", terms.Select(term => "\"" + term + "\""));
    }
}
