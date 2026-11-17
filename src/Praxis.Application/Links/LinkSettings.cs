namespace Praxis.Application.Links;

/// <summary>
/// What Praxis may fetch: nothing (<c>off</c>), or links the owner saved (<c>saved_links</c>,
/// the default). Blocked hosts are never fetched. Links mentioned inside documents are never fetched.
/// </summary>
public sealed record LinkSettings(string Mode, IReadOnlyList<string> BlockedHosts, bool StudyMedia = false,
    bool SampleFrames = false, string? VisionModel = null, int MaximumMediaMinutes = 60)
{
    public const string Off = "off";
    public const string SavedLinks = "saved_links";
    public const int MaximumBlockedHosts = 200;

    public static LinkSettings Default { get; } = new(SavedLinks, []);

    public bool EquivalentTo(LinkSettings other) => Mode == other.Mode && StudyMedia == other.StudyMedia && SampleFrames == other.SampleFrames &&
        VisionModel == other.VisionModel && MaximumMediaMinutes == other.MaximumMediaMinutes && BlockedHosts.SequenceEqual(other.BlockedHosts, StringComparer.OrdinalIgnoreCase);

    public static bool IsValidMode(string mode) => mode is Off or SavedLinks;

    /// <summary>True when the host or one of its parent domains is blocked.</summary>
    public bool Blocks(Uri url) => BlockedHosts.Any(blocked =>
        url.Host.Equals(blocked, StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith("." + blocked, StringComparison.OrdinalIgnoreCase));
}
