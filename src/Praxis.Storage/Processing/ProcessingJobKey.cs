using Microsoft.Data.Sqlite;

namespace Praxis.Storage;

/// <summary>Validation and parameter binding for the (item, revision, processor) job key.</summary>
internal static class ProcessingJobKey
{
    internal static void Validate(Guid itemId, int revision, string processor)
    {
        if (itemId == Guid.Empty || revision < 1)
        {
            throw new ArgumentException("A valid item and revision are required.");
        }

        ValidateProcessor(processor);
    }

    internal static void ValidateProcessor(string processor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processor);
        if (processor.Length > 64 || processor.Any(c =>
            c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
        {
            throw new ArgumentException("Processor keys must be bounded lowercase identifiers.", nameof(processor));
        }
    }

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
