using System.Globalization;
using Praxis.Application.Processing;
using Praxis.Domain.Processing;

namespace Praxis.Infrastructure.Persistence.Processing;

public sealed class SqliteProcessingControls : IProcessingControls
{
    private readonly CatalogDatabase database;
    private readonly TimeProvider clock;
    internal SqliteProcessingControls(CatalogDatabase database, TimeProvider clock) => (this.database, this.clock) = (database, clock);

    internal const string Claimable = """
        NOT EXISTS(SELECT 1 FROM processing_controls control WHERE control.processor_key=j.processor_key AND control.paused=1)
        AND (j.processor_key NOT IN ('intake-triage-v1','intake-understand-v1','semantic-extract-v2','link-fetch-v1')
             OR NOT EXISTS(SELECT 1 FROM agent_runs work WHERE work.kind='dream' AND work.state='running'))
        """;

    public bool IsPaused(string processorKey) => database.ExecuteRead(connection =>
    {
        using var query = connection.CreateCommand($"SELECT NOT ({Claimable}) FROM (SELECT $key AS processor_key) j");
        query.Parameters.AddWithValue("$key", processorKey);
        return Convert.ToInt32(query.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    });

    public void SetPaused(string processorKey, bool paused)
    {
        ProcessingJobKey.ValidateProcessor(processorKey);
        database.Execute(connection => connection.Run(null, """
            INSERT INTO processing_controls(processor_key,paused,actor,changed_utc) VALUES($key,$paused,'owner',$now)
            ON CONFLICT(processor_key) DO UPDATE SET paused=excluded.paused,actor=excluded.actor,changed_utc=excluded.changed_utc
            WHERE processing_controls.paused<>excluded.paused
            """, ("$key", processorKey), ("$paused", paused ? 1 : 0), ("$now", CatalogValues.Timestamp(clock.GetUtcNow()))));
    }

    public ProcessingControl Read(string processorKey) => database.ExecuteRead(connection =>
    {
        using var query = connection.CreateCommand("""
            SELECT COALESCE(c.paused,0),c.actor,c.changed_utc,
                (SELECT COUNT(*) FROM processing_jobs j JOIN items i ON i.item_id=j.item_id JOIN sources s ON s.source_id=i.source_id
                 WHERE j.processor_key=$key AND j.state='running' AND j.lease_until_utc>$now AND s.status='active'),
                (SELECT COUNT(*) FROM processing_jobs j JOIN items i ON i.item_id=j.item_id JOIN sources s ON s.source_id=i.source_id
                 WHERE j.processor_key=$key AND j.state='queued' AND s.status='active'
                   AND j.revision=(SELECT MAX(v.revision) FROM item_versions v WHERE v.item_id=j.item_id)),
                EXISTS(SELECT 1 FROM agent_runs WHERE kind='dream' AND state='running')
            FROM (SELECT 1) LEFT JOIN processing_controls c ON c.processor_key=$key
            """);
        query.Parameters.AddWithValue("$key", processorKey); query.Parameters.AddWithValue("$now", CatalogValues.Timestamp(clock.GetUtcNow())); using var row = query.ExecuteReader(); row.Read();
        bool paused = row.GetBoolean(0);
        int running = row.GetInt32(3);
        bool yielding = row.GetBoolean(5) && processorKey is ProcessorKeys.IntakeTriage or ProcessorKeys.IntakeUnderstand or ProcessorKeys.SemanticExtraction or ProcessorKeys.LinkFetch;
        string state = paused ? running > 0 ? "draining" : "paused" : yielding ? "yielding_to_dream" : "running";
        return new ProcessingControl(processorKey, paused, state, running, row.GetInt32(4), row.IsDBNull(1) ? null : row.GetString(1),
            row.IsDBNull(2) ? null : DateTimeOffset.Parse(row.GetString(2), CultureInfo.InvariantCulture));
    });
}
