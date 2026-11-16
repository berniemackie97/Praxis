namespace Praxis.Application.Agents.Capabilities;

public interface ICapabilityExecutor
{
    Task<CapabilityExecution> ExecuteAsync(CapabilityRequest request, CancellationToken cancellationToken);
}
