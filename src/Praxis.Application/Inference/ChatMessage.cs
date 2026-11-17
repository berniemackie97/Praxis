namespace Praxis.Application.Inference;

public sealed record ChatMessage(ChatRole Role, string Content, IReadOnlyList<string>? Images = null);
