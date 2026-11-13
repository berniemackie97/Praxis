namespace Praxis.Application.Agents.Data;

/// <summary>
/// A structured dataset the agent can query exactly. New domains (calendar, email, health) add a
/// dataset; they never add new query tools.
/// </summary>
public interface IDataset
{
    string Name { get; }

    string Description { get; }

    IReadOnlyList<DataField> Fields { get; }

    DatasetSnapshot Snapshot(AgentToolContext context);
}
