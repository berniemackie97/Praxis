namespace Praxis.Application.Agents;

/// <summary>Safe request-size failure raised before any model request is sent.</summary>
public sealed class AgentRequestTooLargeException(int requestBytes, int maximumRequestBytes) : Exception("The serialized model request exceeds its context budget.")
{
    public int RequestBytes { get; } = requestBytes;
    public int MaximumRequestBytes { get; } = maximumRequestBytes;
}
