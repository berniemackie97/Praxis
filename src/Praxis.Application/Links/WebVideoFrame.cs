namespace Praxis.Application.Links;

/// <summary>One sampled source frame, its local OCR and an explicitly unverified optional model interpretation.</summary>
public sealed record WebVideoFrame(double Seconds, string Text, string ImageBase64, string ImageSha256,
    string? Interpretation = null, string? ModelDigest = null);
