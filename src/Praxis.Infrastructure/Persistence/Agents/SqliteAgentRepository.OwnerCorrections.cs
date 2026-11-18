using System.Text.RegularExpressions;
using Praxis.Application.Agents.Memory;
using Praxis.Domain.Agents;

namespace Praxis.Infrastructure.Persistence.Agents;

public sealed partial class SqliteAgentRepository
{
    public AgentMemory? ReadMemory(Guid memoryId) => database.ExecuteRead(connection =>
    {
        using var command = connection.CreateCommand("SELECT memory_id,text,status,conversation_id,created_utc,owner_statement FROM agent_memories WHERE memory_id=$id");
        command.Parameters.AddWithValue("$id", memoryId.ToString("D"));
        using var row = command.ExecuteReader(); return row.Read() ? ReadMemory(row) : null;
    });

    public AgentMemory SaveOwnerCorrection(Guid conversationId, string statement, bool apply) => database.Execute(connection =>
    {
        if (statement.Length is < 1 or > 4_000)
        {
            throw new ArgumentException("An owner correction must contain 1–4,000 characters.");
        }
        using var existing = connection.CreateCommand("SELECT memory_id FROM agent_memories WHERE owner_statement=$text AND status IN ('active','proposed') ORDER BY status LIMIT 1");
        existing.Parameters.AddWithValue("$text", statement);
        string? found = existing.ExecuteScalar() as string;
        Guid id = found is null ? Guid.NewGuid() : Guid.Parse(found);
        string status = apply ? AgentMemory.Active : AgentMemory.Proposed;
        string title = "Owner correction: " + (statement.Length > 275 ? statement[..274] + "…" : statement);
        connection.Run(null, """
            INSERT INTO agent_memories(memory_id,conversation_id,text,status,created_utc,updated_utc,owner_statement)
            VALUES($id,$conversation,$title,$status,$now,$now,$text)
            ON CONFLICT(memory_id) DO UPDATE SET status=CASE WHEN agent_memories.status='active' THEN 'active' ELSE excluded.status END,updated_utc=excluded.updated_utc
            """, ("$id", id.ToString("D")), ("$conversation", conversationId.ToString("D")), ("$title", title),
            ("$status", status), ("$now", Now()), ("$text", statement));
        using var read = connection.CreateCommand("SELECT memory_id,text,status,conversation_id,created_utc,owner_statement FROM agent_memories WHERE memory_id=$id");
        read.Parameters.AddWithValue("$id", id.ToString("D"));
        using var row = read.ExecuteReader(); row.Read(); return ReadMemory(row);
    });

    public IReadOnlyList<AgentMemory> FindOwnerCorrections(string query, int limit)
    {
        string[] terms = Regex.Matches(ChatMemoryQuery.Build(query), @"[\p{L}\p{N}_]{4,48}", RegexOptions.None, TimeSpan.FromMilliseconds(50))
            .Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
        if (terms.Length == 0)
        {
            return [];
        }
        return database.ExecuteRead(connection =>
                {
                    string matches = string.Join(" OR ", terms.Select((_, index) => $"instr(lower(owner_statement),lower($term{index}))>0"));
                    using var command = connection.CreateCommand($"SELECT memory_id,text,status,conversation_id,created_utc,owner_statement FROM agent_memories WHERE status='active' AND owner_statement IS NOT NULL AND ({matches}) ORDER BY updated_utc DESC LIMIT $limit");
                    command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 3));
                    for (int index = 0; index < terms.Length; index++)
                    {
                        command.Parameters.AddWithValue("$term" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), terms[index]);
                    }
                    using var rows = command.ExecuteReader();
                    List<AgentMemory> results = [];
                    int characters = 0;
                    while (rows.Read())
                    {
                        AgentMemory memory = ReadMemory(rows);
                        characters += memory.OwnerStatement!.Length;
                        if (characters > 4_000)
                        {
                            break;
                        }
                        results.Add(memory);
                    }
                    return results;
                });
    }
}
