namespace Praxis.Application.Agents.Data;

/// <summary>The rows of a dataset visible to the session, how many were withheld by policy, and known gaps.</summary>
public sealed record DatasetSnapshot(IReadOnlyList<DataRow> Rows, int Withheld, IReadOnlyList<string> Gaps);
