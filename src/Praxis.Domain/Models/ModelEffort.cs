namespace Praxis.Domain.Models;

/// <summary>
/// A provider's reasoning effort (for example <c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c>,
/// <c>max</c>). Values come from the provider's own catalog, so only their shape is checked here.
/// </summary>
public static class ModelEffort
{
    public static bool IsValid(string? effort) => effort is null || (effort.Length is >= 1 and <= 16 && effort.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)));
}
