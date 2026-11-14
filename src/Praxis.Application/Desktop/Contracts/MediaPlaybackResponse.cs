using Praxis.Domain.Media;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>What the app plays for a cited media source.</summary>
public sealed record MediaPlaybackResponse(MediaPlaybackReference Playback);
