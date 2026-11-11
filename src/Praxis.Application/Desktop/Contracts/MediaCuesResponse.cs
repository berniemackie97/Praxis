using Praxis.Domain.Media;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>Caption cues bound to captured videos.</summary>
public sealed record MediaCuesResponse(IReadOnlyList<VideoCueHit> Clips);
