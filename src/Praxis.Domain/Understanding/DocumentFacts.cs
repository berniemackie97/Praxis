namespace Praxis.Domain.Understanding;

/// <summary>How a revision was read: its normalized format, page count, title and whether text was truncated.</summary>
public sealed record DocumentFacts(string Format, int? Pages, string? Title, bool Truncated)
{
    public const int MaximumTitleLength = 256;
}
