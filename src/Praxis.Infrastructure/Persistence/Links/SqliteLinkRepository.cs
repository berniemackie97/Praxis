using System.Text.Json;
using Microsoft.Data.Sqlite;
using Praxis.Application.Links;
using Praxis.Domain.Processing;
using Praxis.Infrastructure.Persistence.Processing;

namespace Praxis.Infrastructure.Persistence.Links;

/// <summary>Saved-link fetch results, committed with the job's terminal state, and the owner's link settings.</summary>
public sealed class SqliteLinkRepository : ILinkRepository
{
    private readonly CatalogDatabase database;
    private readonly TimeProvider clock;

    internal SqliteLinkRepository(CatalogDatabase database, TimeProvider clock)
    {
        this.database = database;
        this.clock = clock;
    }

    public LeasedLink? ReadLeased(ProcessingLease lease)
    {
        ProcessingJobKey.Validate(lease.ItemId, lease.Revision, lease.ProcessorKey);
        return database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
                SELECT s.source_id, s.locator, d.content
                FROM processing_jobs j
                JOIN items i ON i.item_id = j.item_id
                JOIN sources s ON s.source_id = i.source_id
                JOIN text_derivatives d ON d.item_id = j.item_id AND d.revision = j.revision
                     AND d.processor_key = 'source-text-v1' AND d.outcome = 'completed'
                WHERE j.item_id = $item AND j.revision = $revision
                  AND j.processor_key = $processor AND j.state = 'running'
                  AND j.lease_key = $lease AND j.lease_until_utc > $now
                  AND s.status = 'active' AND j.revision=(SELECT MAX(v.revision) FROM item_versions v WHERE v.item_id=j.item_id)
                """);
            ProcessingJobParameters.BindLease(command, lease);
            command.Parameters.AddWithValue("$now", Now());
            using SqliteDataReader reader = command.ExecuteReader();
            // Links read before the link reader existed kept their file text; parse that form too.
            return reader.Read() && (SavedLink.FromText(reader.GetString(2))
                                     ?? SavedLink.FromFile(Path.GetExtension(reader.GetString(1)).ToLowerInvariant(),
                                         System.Text.Encoding.UTF8.GetBytes(reader.GetString(2)))) is { } url
                ? new LeasedLink(Guid.Parse(reader.GetString(0)), lease.ItemId, lease.Revision, reader.GetString(1), url)
                : null;
        });
    }

    public bool Complete(ProcessingLease lease, Uri finalUrl, int status, string? contentType, string snapshotLocator) =>
        Finish(lease, "completed", finalUrl, status, contentType, snapshotLocator, null);

    public bool Close(ProcessingLease lease, string reasonCode, int status = 0)
    {
        if (!ProcessingCodes.IsValid(reasonCode))
        {
            throw new ArgumentException("Use a bounded reason code.", nameof(reasonCode));
        }

        return Finish(lease, "unsupported", null, status, null, null, reasonCode);
    }

    public LinkSettings ReadSettings() => database.ExecuteRead(connection =>
    {
        using SqliteCommand command = connection.CreateCommand("SELECT mode, blocked_hosts, study_media, sample_frames, vision_model, maximum_media_minutes FROM link_settings WHERE settings_id = 1");
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? new LinkSettings(reader.GetString(0), JsonSerializer.Deserialize<string[]>(reader.GetString(1)) ?? [], reader.GetBoolean(2), reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5))
            : LinkSettings.Default;
    });

    public void SaveSettings(LinkSettings settings)
    {
        if (!LinkSettings.IsValidMode(settings.Mode) || settings.BlockedHosts.Count > LinkSettings.MaximumBlockedHosts ||
            settings.MaximumMediaMinutes is < 1 or > 240 || settings.VisionModel is { } model && (model.Length is < 1 or > 100 || model.Any(char.IsWhiteSpace)) ||
            settings.BlockedHosts.Any(host => host.Length is < 1 or > 253 || Uri.CheckHostName(host) == UriHostNameType.Unknown))
        {
            throw new ArgumentException("The link settings are invalid.", nameof(settings));
        }

        database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            connection.Run(transaction, """
                INSERT INTO link_settings (settings_id, mode, blocked_hosts, updated_utc) VALUES (1, $mode, $hosts, $now)
                ON CONFLICT(settings_id) DO UPDATE SET mode = excluded.mode, blocked_hosts = excluded.blocked_hosts, updated_utc = excluded.updated_utc
                """, ("$mode", settings.Mode), ("$hosts", JsonSerializer.Serialize(settings.BlockedHosts.Select(host => host.ToLowerInvariant()).Distinct())),
                ("$now", Now()));
            connection.Run(transaction, "UPDATE link_settings SET study_media=$study,sample_frames=$frames,vision_model=$model,maximum_media_minutes=$minutes WHERE settings_id=1",
                ("$study", settings.StudyMedia ? 1 : 0), ("$frames", settings.SampleFrames ? 1 : 0), ("$model", settings.VisionModel), ("$minutes", settings.MaximumMediaMinutes));
            transaction.Commit();
            return true;
        });
    }

    private bool Finish(ProcessingLease lease, string state, Uri? finalUrl, int status, string? contentType, string? snapshot, string? reason)
    {
        ProcessingJobKey.Validate(lease.ItemId, lease.Revision, lease.ProcessorKey);
        string now = Now();
        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            connection.Run(transaction, """
                INSERT INTO link_snapshots (item_id, revision, processor_key, outcome, final_url, http_status, content_type, snapshot_locator, reason, fetched_utc)
                SELECT $item, $revision, $processor, $state, $url, $status, $type, $snapshot, $reason, $now
                WHERE EXISTS (SELECT 1 FROM processing_jobs WHERE item_id = $item AND revision = $revision AND processor_key = $processor
                              AND state = 'running' AND lease_key = $lease AND lease_until_utc > $now)
                ON CONFLICT(item_id, revision, processor_key) DO UPDATE SET outcome = excluded.outcome, final_url = excluded.final_url,
                    http_status = excluded.http_status, content_type = excluded.content_type, snapshot_locator = excluded.snapshot_locator,
                    reason = excluded.reason, fetched_utc = excluded.fetched_utc
                """, ("$item", lease.ItemId.ToString("D")), ("$revision", lease.Revision), ("$processor", lease.ProcessorKey), ("$state", state),
                ("$url", (object?)finalUrl?.AbsoluteUri ?? DBNull.Value), ("$status", status), ("$type", (object?)contentType ?? DBNull.Value),
                ("$snapshot", (object?)snapshot ?? DBNull.Value), ("$reason", (object?)reason ?? DBNull.Value), ("$lease", lease.LeaseKey.ToString("D")), ("$now", now));
            int finished = connection.Run(transaction, """
                UPDATE processing_jobs SET state = $state, lease_key = NULL, lease_until_utc = NULL, failure_code = NULL, updated_utc = $now
                WHERE item_id = $item AND revision = $revision AND processor_key = $processor
                  AND state = 'running' AND lease_key = $lease AND lease_until_utc > $now
                """, ("$state", state), ("$now", now), ("$item", lease.ItemId.ToString("D")),
                ("$revision", lease.Revision), ("$processor", lease.ProcessorKey), ("$lease", lease.LeaseKey.ToString("D")));
            if (finished != 1)
            {
                return false;
            }

            transaction.Commit();
            return true;
        });
    }

    private string Now() => CatalogValues.Timestamp(clock.GetUtcNow());
}
