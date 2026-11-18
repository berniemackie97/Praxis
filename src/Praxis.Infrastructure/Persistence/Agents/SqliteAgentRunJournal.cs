using System.Globalization;
using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Memory;
using Praxis.Domain.Agents;

namespace Praxis.Infrastructure.Persistence.Agents;

/// <summary>Run history and learning eligibility refer to canonical message sequence ranges.</summary>
public sealed partial class SqliteAgentRunJournal : IAgentRunJournal
{
    private const string BackendConfig = "json_object('kind',b.kind,'endpoint',b.endpoint,'keyAccount',b.key_account,'executable',b.executable)";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly CatalogDatabase database;
    private readonly TimeProvider clock;

    internal SqliteAgentRunJournal(CatalogDatabase database, TimeProvider clock) => (this.database, this.clock) = (database, clock);

    public void Enqueue(Guid runId, Guid conversationId, string request, bool useTools, string kind) => database.Execute(connection =>
    {
        if (kind is not ("chat" or "dream") || request.Length is < 1 or > 32_000)
        {
            throw new ArgumentException("The run request is invalid.");
        }
        int inserted = connection.Run(null, $"""
            INSERT INTO agent_runs(run_id,conversation_id,request,use_tools,kind,state,backend_id,backend_config,model,effort,created_utc)
            SELECT $run,c.conversation_id,$request,$tools,$kind,'queued',c.backend_id,{BackendConfig},COALESCE(c.model,b.model),COALESCE(c.effort,b.effort),$now
            FROM agent_conversations c JOIN agent_backends b ON b.backend_id=c.backend_id WHERE c.conversation_id=$conversation
            """, ("$run", Id(runId)), ("$conversation", Id(conversationId)), ("$request", request), ("$tools", useTools ? 1 : 0),
            ("$kind", kind), ("$now", Now()));
        if (inserted != 1)
        {
            throw new ArgumentException("The conversation or backend is unavailable.");
        }
    });

    public bool TryStart(Guid runId) => database.Execute(connection => connection.Run(null, $"""
        UPDATE agent_runs SET state='running',attempt=attempt+1,started_utc=$now,
            message_start=(SELECT COALESCE(MAX(sequence),0)+1 FROM agent_messages WHERE conversation_id=agent_runs.conversation_id)
        WHERE run_id=$run AND state='queued' AND attempt<3 AND EXISTS (
            SELECT 1 FROM agent_conversations c JOIN agent_backends b ON b.backend_id=c.backend_id
            WHERE c.conversation_id=agent_runs.conversation_id AND b.enabled=1 AND agent_runs.backend_config={BackendConfig}
              AND c.backend_id=agent_runs.backend_id AND COALESCE(c.model,b.model) IS agent_runs.model AND COALESCE(c.effort,b.effort) IS agent_runs.effort)
        """, ("$run", Id(runId)), ("$now", Now())) == 1);

    public AgentRunEvent AppendEvent(Guid runId, string kind, string? tool, string? detail) => database.Execute(connection =>
    {
        string? bounded = detail is { Length: > 4_000 } ? detail[..4_000] : detail;
        connection.Run(null, """
            INSERT INTO agent_run_events(run_id,sequence,kind,tool,detail,at_utc)
            SELECT $run,COALESCE(MAX(sequence),0)+1,$kind,$tool,$detail,$now FROM agent_run_events WHERE run_id=$run
            """, ("$run", Id(runId)), ("$kind", kind), ("$tool", tool), ("$detail", bounded), ("$now", Now()));
        using var read = connection.CreateCommand("SELECT MAX(sequence) FROM agent_run_events WHERE run_id=$run");
        read.Parameters.AddWithValue("$run", Id(runId));
        return new AgentRunEvent(Convert.ToInt32(read.ExecuteScalar(), CultureInfo.InvariantCulture), kind, tool, bounded, clock.GetUtcNow());
    });

    public void Finish(Guid runId, string state, AgentReply? reply, string? error) => database.Execute(connection =>
    {
        if (state is not (AgentRunStatus.Completed or AgentRunStatus.Failed or AgentRunStatus.Canceled))
        {
            throw new ArgumentException("The terminal run state is invalid.");
        }
        bool eligible = state == AgentRunStatus.Completed && reply is { StopReason: AgentReply.Completed, Check.Clean: true } &&
            reply.ToolCalls.Count >= 2 && reply.ToolCalls.Any(call => LearningToolReceipt.KindFor(call.Name) is not null);
        using var transaction = connection.BeginTransaction();
        int changed = connection.Run(transaction, """
            UPDATE agent_runs SET state=$state,reply=$reply,error=$error,completed_utc=$now,
                message_end=(SELECT COALESCE(MAX(sequence),0) FROM agent_messages WHERE conversation_id=agent_runs.conversation_id),
                learning_state=CASE WHEN kind='chat' AND $eligible=1 THEN 'pending' ELSE 'ineligible' END
            WHERE run_id=$run AND state IN ('running','queued')
            """, ("$run", Id(runId)), ("$state", state), ("$reply", reply is null ? null : JsonSerializer.Serialize(reply, Json)),
            ("$error", error), ("$now", Now()), ("$eligible", eligible ? 1 : 0));
        if (changed == 1 && state == AgentRunStatus.Completed && reply?.Command is { Name: "dream" } command)
        {
            foreach (var review in command.Reviewed)
            {
                connection.Run(transaction, "INSERT OR IGNORE INTO agent_run_reviews(run_id,entity_id,fingerprint) VALUES($run,$entity,$fingerprint)",
                    ("$run", Id(runId)), ("$entity", Id(review.EntityId)), ("$fingerprint", review.Fingerprint));
            }
        }
        transaction.Commit();
    });

    public AgentRunStatus? Status(Guid runId, int afterSequence) => database.ExecuteRead(connection =>
    {
        using var read = connection.CreateCommand("SELECT conversation_id,state,reply FROM agent_runs WHERE run_id=$run");
        read.Parameters.AddWithValue("$run", Id(runId));
        using var row = read.ExecuteReader();
        if (!row.Read())
        {
            return null;
        }
        Guid conversation = Guid.Parse(row.GetString(0));
        string state = row.GetString(1);
        AgentReply? reply = row.IsDBNull(2) ? null : JsonSerializer.Deserialize<AgentReply>(row.GetString(2), Json);
        row.Close();
        using var events = connection.CreateCommand("SELECT sequence,kind,tool,detail,at_utc FROM agent_run_events WHERE run_id=$run AND sequence>$after ORDER BY sequence LIMIT 1000");
        events.Parameters.AddWithValue("$run", Id(runId)); events.Parameters.AddWithValue("$after", afterSequence);
        using var cursor = events.ExecuteReader();
        List<AgentRunEvent> items = [];
        while (cursor.Read())
        {
            items.Add(new(cursor.GetInt32(0), cursor.GetString(1), cursor.IsDBNull(2) ? null : cursor.GetString(2),
            cursor.IsDBNull(3) ? null : cursor.GetString(3), DateTimeOffset.Parse(cursor.GetString(4), CultureInfo.InvariantCulture)));
        }
        return new AgentRunStatus(runId, conversation, state == "queued" ? AgentRunStatus.Running : state, items, reply);
    });

    public void RecoverInterrupted() => database.Execute(connection =>
    {
        connection.Run(null, """
            UPDATE agent_runs SET state=CASE WHEN kind='dream' AND attempt<3 THEN 'queued' ELSE 'failed' END,
                error='worker_interrupted' WHERE state='running';
            UPDATE agent_runs SET learning_state=CASE WHEN learning_attempt<2 THEN 'pending' ELSE 'failed' END WHERE learning_state='running';
            """);
    });

    public void Suspend(Guid runId) => database.Execute(connection => connection.Run(null,
        "UPDATE agent_runs SET state=CASE WHEN attempt<3 THEN 'queued' ELSE 'failed' END,error='worker_interrupted' WHERE run_id=$run AND kind='dream' AND state='running'", ("$run", Id(runId))));

    public IReadOnlyList<AgentWorkRequest> Pending(int limit) => database.ExecuteRead(connection =>
    {
        using var query = connection.CreateCommand("SELECT run_id,conversation_id,request,use_tools,kind,created_utc FROM agent_runs WHERE state='queued' ORDER BY created_utc LIMIT $limit");
        query.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 20));
        using var reader = query.ExecuteReader();
        List<AgentWorkRequest> pending = [];
        while (reader.Read())
        {
            pending.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetBoolean(3),
            reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
        }
        return pending;
    });

    public bool CancelPending(Guid runId) => database.Execute(connection => connection.Run(null,
        "UPDATE agent_runs SET state='canceled',completed_utc=$now WHERE run_id=$run AND state='queued'", ("$run", Id(runId)), ("$now", Now())) == 1);

    public bool HasPending(Guid conversationId) => Exists("SELECT EXISTS(SELECT 1 FROM agent_runs WHERE conversation_id=$id AND state IN ('queued','running'))", conversationId);
    public bool HasActiveDream() => Exists("SELECT EXISTS(SELECT 1 FROM agent_runs WHERE kind='dream' AND state='running')", Guid.Empty);

    private bool Exists(string sql, Guid id) => database.ExecuteRead(connection =>
    {
        using var query = connection.CreateCommand(sql);
        query.Parameters.AddWithValue("$id", Id(id)); return Convert.ToInt32(query.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    });
    private string Now() => CatalogValues.Timestamp(clock.GetUtcNow());
    private static string Id(Guid id) => id.ToString("D");
}
