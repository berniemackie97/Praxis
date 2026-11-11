using Praxis.Domain.Media;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>Verified header inspection of a captured video.</summary>
public sealed record MediaInspectionResponse(MediaInspection Media);
