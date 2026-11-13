namespace Praxis.Application.Understanding.Profiles;

/// <summary>
/// One record kind as a plugin: the fields the model is asked for, and the code that normalizes
/// them and proves every stated value is printed in the record's evidence.
/// </summary>
public interface IRecordProfile
{
    string Kind { get; }

    /// <summary>How the fields are described to the model.</summary>
    string PromptFields { get; }

    /// <summary>Normalized fields, or null when a required field is missing or a value is not in <paramref name="evidence"/>.</summary>
    IReadOnlyDictionary<string, string>? Normalize(IReadOnlyDictionary<string, string> fields, string evidence);

    /// <summary>The ISO date or date-time a dataset sorts this record by, if any.</summary>
    string? SortKey(IReadOnlyDictionary<string, string> normalized);
}
