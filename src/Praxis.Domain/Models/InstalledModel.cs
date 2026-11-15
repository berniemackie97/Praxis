namespace Praxis.Domain.Models;

public sealed record InstalledModel(string Name, string Digest, long SizeBytes, LocalModelCapabilities? Capabilities = null)
{
    public bool SupportsChat => Capabilities?.Chat ?? !Name.Contains("embed", StringComparison.OrdinalIgnoreCase);
    public bool SupportsEmbedding => Capabilities?.Embedding ?? Name.Contains("embed", StringComparison.OrdinalIgnoreCase);
}
