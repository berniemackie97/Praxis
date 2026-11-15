namespace Praxis.Domain.Models;

/// <summary>Capabilities reported by the local runtime. Missing metadata stays unknown.</summary>
public sealed record LocalModelCapabilities(bool Chat, bool Tools, bool Embedding);
