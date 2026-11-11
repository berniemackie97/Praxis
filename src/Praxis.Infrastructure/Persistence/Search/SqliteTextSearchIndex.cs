using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Praxis.Application.Search;
using Praxis.Domain.Search;

namespace Praxis.Infrastructure.Persistence.Search;

/// <summary>
/// Rebuildable FTS5 index over the latest verified text of active sources. Query-time joins
/// re-check canonical visibility, so a stale index row cannot bypass revocation or supersession.
/// </summary>
public sealed class SqliteTextSearchIndex : ITextSearchIndex
{
    private readonly CatalogDatabase database;

    internal SqliteTextSearchIndex(CatalogDatabase database) => this.database = database;

    public IReadOnlyList<SearchHit> Query(string query, int limit = 20, Guid? collectionId = null)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        ValidateCollection(collectionId);
        string expression = BuildTextQuery(query);
        return database.ExecuteRead(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
                                                                   SELECT s.source_id, v.item_id, v.revision, s.locator, v.digest,
                                                                          snippet(text_search, -1, '⟦', '⟧', '…', 24)
                                                                   FROM text_search
                                                                   JOIN search_index_keys k ON k.rowid = text_search.rowid
                                                                   JOIN item_versions v
                                                                       ON v.item_id = k.item_id AND v.revision = k.revision
                                                                   JOIN items i ON i.item_id = v.item_id
                                                                   JOIN sources s
                                                                       ON s.source_id = i.source_id AND s.source_id = k.source_id
                                                                   JOIN text_derivatives d
                                                                       ON d.item_id = v.item_id AND d.revision = v.revision
                                                                       AND d.source_digest = v.digest AND d.outcome = 'completed'
                                                                       AND d.processor_key = 'source-text-v1'
                                                                   JOIN processing_jobs j
                                                                       ON j.item_id = d.item_id AND j.revision = d.revision
                                                                       AND j.processor_key = d.processor_key AND j.state = 'completed'
                                                                   WHERE text_search MATCH $query
                                                                     AND s.status = 'active'
                                                                     AND ($collection IS NULL OR EXISTS (
                                                                                     SELECT 1 FROM source_collection_memberships membership
                                                                                     JOIN source_collections scope ON scope.collection_id = membership.collection_id
                                                                                     WHERE membership.source_id = s.source_id
                                                                                       AND membership.collection_id = $collection
                                                                                       AND scope.archived_utc IS NULL))
                                                                     AND v.revision = (
                                                                         SELECT MAX(latest.revision) FROM item_versions latest
                                                                         WHERE latest.item_id = v.item_id)
                                                                   ORDER BY bm25(text_search), s.locator COLLATE BINARY,
                                                                            v.item_id, v.revision
                                                                   LIMIT $limit
                                                                   """);
            command.CommandTimeout = 5;
            command.Parameters.AddWithValue("$query", expression);
            command.Parameters.AddWithValue("$collection", (object?)collectionId?.ToString("D") ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader reader = command.ExecuteReader();
            List<SearchHit> hits = new();
            while (reader.Read())
            {
                string locator = reader.GetString(3);
                string snippet = reader.IsDBNull(5) ? "" : reader.GetString(5);
                if (snippet.Length > 480)
                {
                    snippet = snippet[..480];
                }

                hits.Add(new SearchHit(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                    Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                    Path.GetFileName(locator), locator, reader.GetString(4), snippet));
            }

            return hits;
        });
    }

    public IReadOnlyList<SearchHit> RetainCurrent(IReadOnlyList<SearchHit> candidates, Guid? collectionId = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ValidateCollection(collectionId);
        if (candidates.Count > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(candidates));
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        return database.ExecuteRead(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            string[] parameters = new string[candidates.Count];
            for (int index = 0; index < candidates.Count; index++)
            {
                string name = "$item" + index.ToString(CultureInfo.InvariantCulture);
                parameters[index] = name;
                command.Parameters.AddWithValue(name, candidates[index].ItemId.ToString("D"));
            }

            command.CommandText = """
                SELECT e.item_id, e.source_id, e.revision, v.digest, e.locator
                FROM eligible_text_search e
                JOIN item_versions v ON v.item_id = e.item_id AND v.revision = e.revision
                WHERE e.item_id IN (
                """ + string.Join(", ", parameters) + ")\n                AND ($collection IS NULL OR EXISTS (\n                                SELECT 1 FROM source_collection_memberships membership\n                                JOIN source_collections scope ON scope.collection_id = membership.collection_id\n                                WHERE membership.source_id = e.source_id\n                                  AND membership.collection_id = $collection\n                                  AND scope.archived_utc IS NULL))";
            command.CommandTimeout = 5;
            command.Parameters.AddWithValue("$collection", (object?)collectionId?.ToString("D") ?? DBNull.Value);
            using SqliteDataReader reader = command.ExecuteReader();
            Dictionary<Guid, (Guid Source, int Revision, string Digest, string Locator)> current = new();
            while (reader.Read())
            {
                current.Add(Guid.Parse(reader.GetString(0)),
                    (Guid.Parse(reader.GetString(1)), reader.GetInt32(2),
                        reader.GetString(3), reader.GetString(4)));
            }

            return candidates.Where(hit =>
                current.TryGetValue(hit.ItemId, out var eligible) &&
                eligible.Source == hit.SourceId &&
                eligible.Revision == hit.Revision &&
                string.Equals(eligible.Digest, hit.OriginalSha256, StringComparison.Ordinal) &&
                string.Equals(eligible.Locator, hit.SourceLocator, StringComparison.Ordinal))
                .ToArray();
        });
    }

    public int Rebuild(int maxDocuments = 100_000, long maxTextBytes = 512L * 1024 * 1024)
    {
        if (maxDocuments is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDocuments));
        }

        if (maxTextBytes is < 1 or > 512L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTextBytes));
        }

        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand count = connection.CreateCommand("""
                                                                 SELECT COUNT(*), COALESCE(SUM(length(CAST(content AS BLOB))), 0)
                                                                 FROM eligible_text_search
                                                                 """);
            count.Transaction = transaction;
            count.CommandTimeout = 30;
            int eligible;
            using (SqliteDataReader reader = count.ExecuteReader())
            {
                reader.Read();
                long documentCount = reader.GetInt64(0);
                if (documentCount > maxDocuments || reader.GetInt64(1) > maxTextBytes)
                {
                    throw new InvalidOperationException("The rebuild exceeds its data budget.");
                }

                eligible = (int)documentCount;
            }

            using SqliteCommand clear = connection.CreateCommand("DELETE FROM text_search");
            clear.Transaction = transaction;
            clear.CommandTimeout = 30;
            clear.ExecuteNonQuery();

            using SqliteCommand removeKeys = connection.CreateCommand("DELETE FROM search_index_keys");
            removeKeys.Transaction = transaction;
            removeKeys.CommandTimeout = 30;
            removeKeys.ExecuteNonQuery();

            using SqliteCommand insertKeys = connection.CreateCommand("""
                                                                      INSERT INTO search_index_keys (item_id, revision, source_id)
                                                                      SELECT item_id, revision, source_id
                                                                      FROM eligible_text_search ORDER BY item_id, revision
                                                                      """);
            insertKeys.Transaction = transaction;
            insertKeys.CommandTimeout = 30;
            insertKeys.ExecuteNonQuery();

            using SqliteCommand populate = connection.CreateCommand("""
                                                                    INSERT INTO text_search (rowid, locator, content)
                                                                    SELECT k.rowid, e.locator, e.content
                                                                    FROM eligible_text_search e
                                                                    JOIN search_index_keys k
                                                                        ON k.item_id = e.item_id AND k.revision = e.revision
                                                                    ORDER BY e.item_id, e.revision
                                                                    """);
            populate.Transaction = transaction;
            populate.CommandTimeout = 30;
            populate.ExecuteNonQuery();
            transaction.Commit();
            return eligible;
        });
    }

    private static void ValidateCollection(Guid? collectionId)
    {
        if (collectionId == Guid.Empty)
        {
            throw new ArgumentException("A nonempty collection is required.", nameof(collectionId));
        }
    }

    private static string BuildTextQuery(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Length > 256 || query.Any(char.IsControl))
        {
            throw new ArgumentException("Search queries must be bounded printable text.", nameof(query));
        }

        List<string> terms = new();
        StringBuilder token = new();
        foreach (char character in query)
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

        return terms.Count is < 1 or > 16
            ? throw new ArgumentException("Search queries must contain one to sixteen terms.", nameof(query))
            : string.Join(" AND ", terms.Select(term => "\"" + term + "\""));
    }
}
