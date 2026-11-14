namespace Praxis.Application.Policy;

/// <summary>
/// When the local tier drafts a document before the strong tier reviews it: <c>auto</c> only when
/// this Mac is measured to finish within the time budget, <c>always</c> for every small span, or
/// <c>off</c> so the strong tier reads directly.
/// </summary>
public static class LocalDraftModes
{
    public const string Auto = "auto";
    public const string Always = "always";
    public const string Off = "off";

    public static bool IsValid(string? mode) => mode is Auto or Always or Off;
}
