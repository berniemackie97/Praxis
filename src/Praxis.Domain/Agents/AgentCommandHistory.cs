namespace Praxis.Domain.Agents;

/// <summary>Attaches a durable command receipt to its final canonical message sequence.</summary>
public sealed record AgentCommandHistory(int MessageSequence, AgentCommandReceipt Receipt);
