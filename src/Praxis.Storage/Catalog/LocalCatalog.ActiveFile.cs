namespace Praxis.Storage;

public sealed partial class LocalCatalog
{
    private (Guid ItemId, int Revision, string Digest, long Length, string Locator)?
        CurrentFileSource(Guid sourceId)
    {
        using var query = connection.CreateCommand("""
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
