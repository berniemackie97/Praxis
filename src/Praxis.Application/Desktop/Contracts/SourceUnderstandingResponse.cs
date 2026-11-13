using Praxis.Domain.Understanding;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>How intake read, triaged and understood a source's current revision, or null before it was read.</summary>
public sealed record SourceUnderstandingResponse(SourceUnderstanding? Understanding);
