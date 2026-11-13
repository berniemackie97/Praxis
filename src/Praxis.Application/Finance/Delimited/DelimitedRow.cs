namespace Praxis.Application.Finance.Delimited;

/// <summary>One record of a delimited export and the physical line where it begins.</summary>
public sealed record DelimitedRow(int Line, IReadOnlyList<string> Fields);
