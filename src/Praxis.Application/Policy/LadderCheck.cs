namespace Praxis.Application.Policy;

/// <summary>
/// Code verification of one tier's answer: the verified value and whether it is good enough to
/// accept without asking a stronger tier. An unacceptable value is still kept as a partial result.
/// </summary>
public sealed record LadderCheck<T>(T Value, bool Acceptable) where T : class;
