namespace Praxis.Domain.Processing;

/// <summary>Validation of the (item, revision, processor) key that identifies a processing job.</summary>
public static class ProcessingJobKey
{
    public static void Validate(Guid itemId, int revision, string processor)
    {
        if (itemId == Guid.Empty || revision < 1)
        {
            throw new ArgumentException("A valid item and revision are required.");
        }

        ValidateProcessor(processor);
    }

    public static void ValidateProcessor(string processor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processor);
        if (processor.Length > 64 || processor.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
        {
            throw new ArgumentException("Processor keys must be bounded lowercase identifiers.", nameof(processor));
        }
    }
}
