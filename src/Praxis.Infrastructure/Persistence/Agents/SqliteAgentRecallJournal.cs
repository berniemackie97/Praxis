using System.Text.Json;
using Microsoft.Data.Sqlite;
using Praxis.Application.Agents;
using Praxis.Domain.Agents;

namespace Praxis.Infrastructure.Persistence.Agents;

public sealed class SqliteAgentRecallJournal : IAgentRecallJournal
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly CatalogDatabase database;

    internal SqliteAgentRecallJournal(CatalogDatabase database) => this.database = database;

    public void Record(Guid conversationId, AgentRecallReceipt receipt) => database.Execute(connection =>
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        string id = conversationId.ToString("D");
        connection.Run(transaction, """
            INSERT INTO agent_brain_recalls (id, conversation_id, receipt) VALUES ($id, $conversation, $receipt)
            """, ("$id", receipt.Id.ToString("D")), ("$conversation", id), ("$receipt", JsonSerializer.Serialize(receipt, Json)));
        connection.Run(transaction, """
            DELETE FROM agent_brain_recalls WHERE conversation_id = $conversation AND rowid IN
            (SELECT rowid FROM agent_brain_recalls WHERE conversation_id = $conversation ORDER BY rowid DESC LIMIT -1 OFFSET 100)
            """, ("$conversation", id));
        transaction.Commit();
    });

    public IReadOnlyList<AgentRecallReceipt> Read(Guid conversationId, int limit) => database.ExecuteRead<IReadOnlyList<AgentRecallReceipt>>(connection =>
    {
        using SqliteCommand command = connection.CreateCommand("""
            SELECT receipt FROM agent_brain_recalls WHERE conversation_id = $conversation ORDER BY rowid DESC LIMIT $limit
            """);
        command.Parameters.AddWithValue("$conversation", conversationId.ToString("D"));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        using SqliteDataReader reader = command.ExecuteReader();
        List<AgentRecallReceipt> receipts = [];
        while (reader.Read())
        {
            if (JsonSerializer.Deserialize<AgentRecallReceipt>(reader.GetString(0), Json) is { } item)
            {
                receipts.Add(item);
            }
        }

        return receipts;
    });
}
