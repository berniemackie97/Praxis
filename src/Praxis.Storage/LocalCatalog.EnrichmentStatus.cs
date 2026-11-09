namespace Praxis.Storage;

public sealed partial class LocalCatalog
{
    public string TopicProcessingOutcome(Guid sourceId)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("A valid source identifier is required.", nameof(sourceId));
        }

        lock (connection)
        {
            using var query = Command(connection, """
                SELECT s.status, j.state, d.outcome, r.outcome
                FROM sources s
                JOIN items i ON i.source_id = s.source_id
                JOIN item_versions v ON v.item_id = i.item_id
                LEFT JOIN processing_jobs j ON j.item_id = v.item_id
                    AND j.revision = v.revision AND j.processor_key = 'source-text-v1'
                LEFT JOIN text_derivatives d ON d.item_id = v.item_id
                    AND d.revision = v.revision AND d.processor_key = 'source-text-v1'
                    AND d.source_digest = v.digest
                LEFT JOIN enrichment_runs r ON r.item_id = v.item_id
                    AND r.revision = v.revision AND r.source_digest = v.digest
                    AND r.processor_key = $producer
                WHERE s.source_id = $source
                  AND v.revision = (SELECT MAX(latest.revision)
                                    FROM item_versions latest WHERE latest.item_id = v.item_id)
                LIMIT 1
                """);
            query.Parameters.AddWithValue("$producer", TopicProducer);
            query.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            using var reader = query.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != "active")
            {
                return "unavailable";
            }

            if (!reader.IsDBNull(3))
            {
                return reader.GetString(3);
            }

            if (!reader.IsDBNull(2) && reader.GetString(2) == "unsupported")
            {
                return "unsupported";
            }

            if (!reader.IsDBNull(1) && reader.GetString(1) is "failed" or "canceled")
            {
                return "failed";
            }

            return "pending";
        }
    }
}
