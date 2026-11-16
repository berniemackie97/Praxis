namespace Praxis.Application.Agents.Capabilities;

public interface ICapabilityJournal
{
    CapabilityRequest Propose(Guid conversationId, string operation, string? target, string description);
    IReadOnlyList<CapabilityRequest> List(int limit);
    CapabilityRequest? Read(Guid id);
    CapabilityRequest? Claim(Guid id, bool accept);
    void Finish(Guid id, CapabilityExecution result);
}
