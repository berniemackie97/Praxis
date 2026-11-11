namespace Praxis.Domain.Processing;

/// <summary>
/// Failure and reason codes persisted with processing outcomes. Codes are bounded lowercase
/// identifiers so that exception text or source content can never be stored in their place.
/// </summary>
public static class ProcessingCodes
{
    public const int MaximumLength = 64;

    public static bool IsValid(string? code) =>
        !string.IsNullOrEmpty(code) && code.Length <= MaximumLength &&
        code.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}
