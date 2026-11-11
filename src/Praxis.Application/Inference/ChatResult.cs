namespace Praxis.Application.Inference;

public sealed record ChatResult(string Content, int InputTokens, int OutputTokens, TimeSpan Duration);
