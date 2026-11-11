using Microsoft.Data.Sqlite;
using Praxis.Domain.Processing;

namespace Praxis.Infrastructure.Persistence.Processing;

/// <summary>Binds the (item, revision, processor) job key and its lease to SQL parameters.</summary>
internal static class ProcessingJobParameters
{
    internal static void Bind(SqliteCommand command, Guid itemId, int revision, string processor)
    {
        command.Parameters.AddWithValue("$item", itemId.ToString("D"));
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$processor", processor);
    }

    internal static void BindLease(SqliteCommand command, ProcessingLease lease)
    {
        Bind(command, lease.ItemId, lease.Revision, lease.ProcessorKey);
        command.Parameters.AddWithValue("$lease", lease.LeaseKey.ToString("D"));
    }
}
