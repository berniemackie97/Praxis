namespace Praxis.Application.Activity;

/// <summary>How many current files ended intake with one status and reason, such as understood or waiting for a model.</summary>
public sealed record IntakeOutcomeCount(string Status, string? Reason, int Count);
