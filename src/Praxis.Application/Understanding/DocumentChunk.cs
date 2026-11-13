namespace Praxis.Application.Understanding;

/// <summary>A span of the document text understood in one call.</summary>
public sealed record DocumentChunk(int Index, int Offset, int Length);
