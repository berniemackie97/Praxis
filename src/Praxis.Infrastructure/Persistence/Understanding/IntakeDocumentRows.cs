using Microsoft.Data.Sqlite;
using Praxis.Domain.Understanding;

namespace Praxis.Infrastructure.Persistence.Understanding;

/// <summary>Writes to <c>intake_documents</c> shared by the read, triage and understand stages, inside the caller's transaction.</summary>
internal static class IntakeDocumentRows
{
    internal static void UpsertRead(SqliteConnection connection, SqliteTransaction transaction, Guid itemId, int revision, string digest, DocumentFacts facts, string now) =>
        connection.Run(transaction, """
            INSERT INTO intake_documents (item_id, revision, source_digest, format, pages, title, truncated, status, updated_utc)
            VALUES ($item, $revision, $digest, $format, $pages, $title, $truncated, 'read', $now)
            ON CONFLICT(item_id, revision) DO UPDATE
            SET source_digest = excluded.source_digest, format = excluded.format, pages = excluded.pages, title = excluded.title,
                truncated = excluded.truncated, updated_utc = excluded.updated_utc
            """, ("$item", itemId.ToString("D")), ("$revision", revision), ("$digest", digest), ("$format", facts.Format),
            ("$pages", facts.Pages), ("$title", facts.Title), ("$truncated", facts.Truncated ? 1 : 0), ("$now", now));

    /// <summary>Creates the row for a revision read before intake tracking existed (format unknown, treated as text).</summary>
    internal static void Ensure(SqliteConnection connection, SqliteTransaction transaction, Guid itemId, int revision, string digest, string now) =>
        connection.Run(transaction, """
            INSERT INTO intake_documents (item_id, revision, source_digest, format, status, updated_utc)
            VALUES ($item, $revision, $digest, 'text', 'read', $now)
            ON CONFLICT(item_id, revision) DO NOTHING
            """, ("$item", itemId.ToString("D")), ("$revision", revision), ("$digest", digest), ("$now", now));
}
