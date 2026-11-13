namespace Praxis.Domain.Understanding;

/// <summary>Typed record kinds intake extracts. Each has a profile that defines, normalizes and verifies it.</summary>
public static class RecordKinds
{
    public const string Transaction = "transaction";
    public const string Event = "event";
    public const string Deadline = "deadline";
    public const string Contact = "contact";

    public static readonly IReadOnlyList<string> All = [Transaction, Event, Deadline, Contact];

    public static bool IsValid(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);
}
