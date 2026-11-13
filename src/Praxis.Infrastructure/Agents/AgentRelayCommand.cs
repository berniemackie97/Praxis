namespace Praxis.Infrastructure.Agents;

/// <summary>
/// How a CLI agent starts the Praxis MCP relay: the worker executable and any leading arguments
/// (for example the assembly path when hosted by <c>dotnet</c>), followed by <c>mcp-relay</c>.
/// </summary>
public sealed record AgentRelayCommand(string Executable, IReadOnlyList<string> LeadingArguments);
