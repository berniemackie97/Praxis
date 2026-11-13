namespace Praxis.Domain.Agents;

/// <summary>
/// Post-answer verification: figures in the answer that no tool returned or computed, and whether
/// evidence was available but not cited. A clean check has no unsupported figures.
/// </summary>
public sealed record AnswerCheck(IReadOnlyList<string> UnsupportedFigures, bool MissingCitations, bool Repaired)
{
    public bool Clean => UnsupportedFigures.Count == 0 && !MissingCitations;
}
