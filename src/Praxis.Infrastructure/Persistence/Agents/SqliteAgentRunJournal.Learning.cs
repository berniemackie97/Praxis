using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Praxis.Application.Agents;
using Praxis.Domain.Agents;

namespace Praxis.Infrastructure.Persistence.Agents;

public sealed partial class SqliteAgentRunJournal
{
    public AgentLearningEpisode? ClaimLearning(int dailyCalls, int dailyTokens, DateTimeOffset? completedBefore = null) => database.Execute(connection =>
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        string day = CatalogValues.Timestamp(new DateTimeOffset(clock.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero));
        using var budget = connection.CreateCommand("SELECT COUNT(*),COALESCE(SUM(MAX(6000,input_tokens+output_tokens)),0) FROM agent_learning_attempts WHERE started_utc >= $day");
        budget.Transaction = transaction; budget.Parameters.AddWithValue("$day", day);
        using var used = budget.ExecuteReader(); used.Read();
        bool exhausted = used.GetInt32(0) >= dailyCalls || used.GetInt32(1) + 6_000 > dailyTokens;
        used.Close();
        if (exhausted)
        {
            return null;
        }
        using var next = connection.CreateCommand("SELECT run_id,conversation_id,request,reply,message_start,message_end,started_utc,completed_utc FROM agent_runs WHERE learning_state='pending' AND learning_attempt<2 AND completed_utc <= $before ORDER BY completed_utc LIMIT 1");
        next.Transaction = transaction;
        next.Parameters.AddWithValue("$before", CatalogValues.Timestamp(completedBefore ?? clock.GetUtcNow()));
        using var row = next.ExecuteReader();
        if (!row.Read())
        {
            return null;
        }
        Guid run = Guid.Parse(row.GetString(0)), conversation = Guid.Parse(row.GetString(1));
        string request = row.GetString(2);
        AgentReply reply = JsonSerializer.Deserialize<AgentReply>(row.GetString(3), Json)!;
        int first = row.GetInt32(4), last = row.GetInt32(5);
        string started = row.GetString(6), completed = row.GetString(7);
        row.Close();
        connection.Run(transaction, "UPDATE agent_runs SET learning_state='running',learning_attempt=learning_attempt+1,learning_started_utc=$now WHERE run_id=$run",
            ("$run", Id(run)), ("$now", Now()));
        connection.Run(transaction, "INSERT INTO agent_learning_attempts(run_id,attempt,started_utc) SELECT run_id,learning_attempt,$now FROM agent_runs WHERE run_id=$run", ("$run", Id(run)), ("$now", Now()));
        using var messages = connection.CreateCommand("SELECT role,content,tool_calls,tool_call_id,citations FROM agent_messages WHERE conversation_id=$conversation AND sequence BETWEEN $first AND $last ORDER BY sequence LIMIT 100");
        messages.Transaction = transaction;
        messages.Parameters.AddWithValue("$conversation", Id(conversation)); messages.Parameters.AddWithValue("$first", first); messages.Parameters.AddWithValue("$last", last);
        using var rows = messages.ExecuteReader();
        List<AgentMessage> transcript = [];
        while (rows.Read())
        {
            transcript.Add(new(rows.GetString(0), rows.GetString(1), JsonSerializer.Deserialize<AgentToolCall[]>(rows.GetString(2), Json)!,
            rows.IsDBNull(3) ? null : rows.GetString(3), JsonSerializer.Deserialize<AgentCitation[]>(rows.GetString(4), Json)!));
        }
        rows.Close();
        using var audited = connection.CreateCommand("""
            SELECT call_id,tool FROM agent_tool_calls
            WHERE conversation_id=$conversation AND started_utc >= $started AND started_utc <= $completed
              AND outcome='ok' AND withheld=0
            """);
        audited.Transaction = transaction;
        audited.Parameters.AddWithValue("$conversation", Id(conversation));
        audited.Parameters.AddWithValue("$started", started);
        audited.Parameters.AddWithValue("$completed", completed);
        using var checks = audited.ExecuteReader();
        HashSet<(string CallId, string Tool)> auditedCalls = [];
        while (checks.Read())
        {
            auditedCalls.Add((checks.GetString(0), checks.GetString(1)));
        }
        checks.Close(); transaction.Commit();
        return new AgentLearningEpisode(run, conversation, request, reply, transcript, auditedCalls);
    });

    public bool EvidenceCurrent(Guid runId) => database.ExecuteRead(connection =>
    {
        using var query = connection.CreateCommand($"SELECT EXISTS(SELECT 1 FROM agent_runs r WHERE r.run_id=$run AND r.state='completed' AND ({AgentEvidenceSql.CurrentRun}))");
        query.Parameters.AddWithValue("$run", Id(runId)); return Convert.ToInt32(query.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    });

    public void FinishLearning(Guid runId, string outcome, Guid? skillRevision, int inputTokens, int outputTokens, string? model) => database.Execute(connection =>
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        connection.Run(transaction, "UPDATE agent_learning_attempts SET input_tokens=$input,output_tokens=$output,model=$model,outcome=$state WHERE run_id=$run AND attempt=(SELECT learning_attempt FROM agent_runs WHERE run_id=$run)",
            ("$run", Id(runId)), ("$input", inputTokens), ("$output", outputTokens), ("$model", model), ("$state", outcome));
        connection.Run(transaction, """
            UPDATE agent_runs SET learning_state=$state,learning_skill=$skill,learning_input=learning_input+$input,learning_output=learning_output+$output,
                learning_model=$model,learning_reviewed_utc=$now WHERE run_id=$run AND learning_state='running'
            """, ("$run", Id(runId)), ("$state", outcome), ("$skill", skillRevision?.ToString("D")), ("$input", inputTokens),
            ("$output", outputTokens), ("$model", model), ("$now", Now()));
        transaction.Commit();
    });

    public IReadOnlyList<AgentLearningReceipt> LearningHistory(int limit) => database.ExecuteRead(connection =>
    {
        using var query = connection.CreateCommand("SELECT run_id,conversation_id,learning_state,learning_skill,learning_input,learning_output,learning_model,learning_reviewed_utc FROM agent_runs WHERE learning_state<>'ineligible' ORDER BY completed_utc DESC LIMIT $limit");
        query.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        using var rows = query.ExecuteReader(); List<AgentLearningReceipt> history = [];
        while (rows.Read())
        {
            history.Add(new(Guid.Parse(rows.GetString(0)), Guid.Parse(rows.GetString(1)), rows.GetString(2), rows.IsDBNull(3) ? null : Guid.Parse(rows.GetString(3)),
            rows.GetInt32(4), rows.GetInt32(5), rows.IsDBNull(6) ? null : rows.GetString(6), rows.IsDBNull(7) ? null : DateTimeOffset.Parse(rows.GetString(7), CultureInfo.InvariantCulture)));
        }
        return history;
    });

}
