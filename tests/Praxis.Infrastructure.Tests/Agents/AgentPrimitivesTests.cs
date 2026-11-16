using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Compute;
using Praxis.Application.Agents.Data;
using Praxis.Application.Agents.Mcp;
using Praxis.Application.Agents.Runtime;
using Praxis.Application.Agents.Tools;
using Praxis.Domain.Agents;
using Praxis.Domain.Policy;
using Xunit;

namespace Praxis.Infrastructure.Tests.Agents;

public sealed class AgentPrimitivesTests
{
    [Theory]
    [InlineData("1 + 2 * 3", "7")]
    [InlineData("(1 + 2) * 3", "9")]
    [InlineData("1250 * 30%", "375.00")]
    [InlineData("-5 + 2", "-3")]
    [InlineData("round(10 / 3, 2)", "3.33")]
    [InlineData("round(2.5)", "2")]
    [InlineData("sum(1, 2, 3.5) - max(1, 4)", "2.5")]
    [InlineData("1,234.50 + 0.50", "1235.00")]
    [InlineData("0.1 + 0.2", "0.3")]
    public void DecimalExpressionsAreExact(string expression, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), DecimalExpression.Evaluate(expression));

    [Theory]
    [InlineData("1 / 0")]
    [InlineData("1 +")]
    [InlineData("unknown + 1")]
    [InlineData("pow(2, 3)")]
    [InlineData("(((((((((((((((((((((((((((((((((1)))))))))))))))))))))))))))))))))")]
    [InlineData("")]
    public void InvalidExpressionsFailWithFixableMessages(string expression) =>
        Assert.Throws<ArgumentException>(() => DecimalExpression.Evaluate(expression));

    [Fact]
    public async Task ComputeToolChainsLabelledResults()
    {
        AgentToolOutput output = await new ComputeEvaluateTool().InvokeAsync(Json("""
            {"variables":{"dining":"412.80"},"expressions":{"cut":"dining * 30%","remaining":"dining - cut"}}
            """), Context(), CancellationToken.None);
        using JsonDocument results = JsonDocument.Parse(output.Content);
        Assert.Equal(123.84m, decimal.Parse(results.RootElement.GetProperty("results").GetProperty("cut").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(288.96m, decimal.Parse(results.RootElement.GetProperty("results").GetProperty("remaining").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void DataEngineFiltersGroupsAndSumsExactlyPerCurrency()
    {
        FakeDataset dataset = new([
            Row("2026-10-03", "groceries", -5421, "USD"), Row("2026-10-21", "groceries", -9612, "USD"),
            Row("2026-10-01", "income", 250000, "USD"), Row("2026-11-02", "groceries", -1000, "USD"), Row("2026-10-05", "groceries", -700, "EUR")
        ]);
        object result = DataQueryEngine.Run(dataset, dataset.Snapshot(Context()),
            [new DataFilter("date", "between", ["2026-10-01", "2026-10-31"]), new DataFilter("category", "eq", ["groceries"])],
            ["category"], [new DataMetric("sum", "amount"), new DataMetric("count", null)], null, false, 50, 0, out _);
        string json = ToolJson.Serialize(result);
        Assert.Contains("\"sum_amount\":-15033", json, StringComparison.Ordinal);
        Assert.Contains("\"sum_amount\":-700", json, StringComparison.Ordinal);
        Assert.Contains("\"currency\":\"EUR\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DataEngineRejectsUnknownFieldsAndBadValuesWithGuidance()
    {
        FakeDataset dataset = new([Row("2026-10-03", "groceries", -5421, "USD")]);
        DatasetSnapshot snapshot = dataset.Snapshot(Context());
        ArgumentException unknown = Assert.Throws<ArgumentException>(() =>
            DataQueryEngine.Run(dataset, snapshot, [new DataFilter("colour", "eq", ["red"])], [], [], null, false, 10, 0, out _));
        Assert.Contains("Fields:", unknown.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() =>
            DataQueryEngine.Run(dataset, snapshot, [new DataFilter("amount", "gt", ["twelve"])], [], [], null, false, 10, 0, out _));
        Assert.Throws<ArgumentException>(() =>
            DataQueryEngine.Run(dataset, snapshot, [], ["amount"], [], null, false, 10, 0, out _));
    }

    [Fact]
    public void AnswerCheckAcceptsFiguresFromToolsIncludingMinorUnitsAndFlagsInventedOnes()
    {
        string[] tools = ["""{"sum_amount":-15033,"count":2}""", """{"results":{"cut":"45.10"}}"""];
        AnswerCheck clean = AnswerVerifier.Check("Groceries cost $150.33 across 2 purchases [1]; cutting saves $45.10 [2].", tools, "budget for 2026", true, false);
        Assert.True(clean.Clean);
        AnswerCheck invented = AnswerVerifier.Check("Groceries cost $180.00 [1] in 2026.", tools, "", true, false);
        Assert.Equal(["$180.00"], invented.UnsupportedFigures);
        Assert.True(AnswerVerifier.Check("Groceries cost $150.33.", tools, "", true, false).MissingCitations);
        Assert.Contains("$180.00", AnswerVerifier.RepairInstruction(invented), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegistryEnforcesTiersRejectsBadInputBoundsOutputAndAudits()
    {
        RecordingAudit audit = new();
        AgentToolRegistry registry = new([new EchoTool(ToolTiers.Write), new EchoTool(ToolTiers.Read, "big"), .. SystemActionTools.All], audit, TimeProvider.System);
        AgentToolOutput denied = await registry.InvokeAsync(new AgentToolCall("1", "echo_write", "{}"), Context(ToolPermissions.ReadOnly), CancellationToken.None);
        Assert.Contains("permission_denied", denied.Content, StringComparison.Ordinal);
        AgentToolOutput allowed = await registry.InvokeAsync(new AgentToolCall("2", "echo_write", "{\"text\":\"hi\"}"), Context(ToolPermissions.AllowEdits), CancellationToken.None);
        Assert.Contains("hi", allowed.Content, StringComparison.Ordinal);
        Assert.Contains("disabled", (await registry.InvokeAsync(new AgentToolCall("3", "system_run_command", "{\"command\":\"ls\"}"),
            Context(ToolPermissions.AllowEdits), CancellationToken.None)).Content, StringComparison.Ordinal);
        Assert.Contains("unknown_tool", (await registry.InvokeAsync(new AgentToolCall("4", "nope", "{}"), Context(), CancellationToken.None)).Content, StringComparison.Ordinal);
        Assert.Contains("invalid_arguments", (await registry.InvokeAsync(new AgentToolCall("5", "echo_write", "[1,2"), Context(ToolPermissions.AllowEdits), CancellationToken.None)).Content, StringComparison.Ordinal);
        AgentToolOutput big = await registry.InvokeAsync(new AgentToolCall("6", "echo_big", "{\"text\":\"x\",\"repeat\":50000}"), Context(), CancellationToken.None);
        Assert.True(big.Content.Length <= AgentToolRegistry.MaximumResultCharacters);
        using JsonDocument parsed = JsonDocument.Parse(big.Content);
        Assert.True(parsed.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(["permission_denied", "ok", "permission_denied", "unknown_tool", "invalid_arguments", "truncated"], audit.Records.Select(record => record.Outcome));
    }

    [Fact]
    public void CitationLedgerNumbersEvidenceOnceAndWrapsResults()
    {
        CitationLedger ledger = new();
        AgentCitation citation = new(Guid.NewGuid(), Guid.NewGuid(), 1, "row:5", "bank.csv");
        string first = ledger.Wrap(new AgentToolOutput("{\"a\":1}", [citation], [new WithheldNotice(2, WithheldNotice.DisclosurePolicy)]));
        string second = ledger.Wrap(new AgentToolOutput("{\"b\":2}", [citation], []));
        using JsonDocument wrapped = JsonDocument.Parse(first);
        Assert.Equal(1, wrapped.RootElement.GetProperty("citations")[0].GetProperty("n").GetInt32());
        Assert.Equal(2, wrapped.RootElement.GetProperty("withheld")[0].GetProperty("count").GetInt32());
        Assert.Contains("\"n\":1", second, StringComparison.Ordinal);
        Assert.Single(ledger.All);
    }

    [Fact]
    public async Task McpProtocolServesInitializeListAndCallsThroughTheInvokeFunction()
    {
        List<AgentToolCall> calls = [];
        McpServerProtocol protocol = new([new EchoTool(ToolTiers.Read).Definition], (call, _) =>
        {
            calls.Add(call);
            return Task.FromResult("""{"result":{"echo":"hi"}}""");
        });
        using JsonDocument init = JsonDocument.Parse((await protocol.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""", CancellationToken.None))!);
        Assert.Equal("praxis", init.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Null(await protocol.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", CancellationToken.None));
        using JsonDocument list = JsonDocument.Parse((await protocol.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", CancellationToken.None))!);
        Assert.Equal("echo_read", list.RootElement.GetProperty("result").GetProperty("tools")[0].GetProperty("name").GetString());
        using JsonDocument call = JsonDocument.Parse((await protocol.HandleAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo_read","arguments":{"text":"hi"}}}""", CancellationToken.None))!);
        Assert.False(call.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("{\"text\":\"hi\"}", Assert.Single(calls).ArgumentsJson);
        using JsonDocument unknown = JsonDocument.Parse((await protocol.HandleAsync("""{"jsonrpc":"2.0","id":4,"method":"resources/list"}""", CancellationToken.None))!);
        Assert.Equal(-32601, unknown.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        using JsonDocument bad = JsonDocument.Parse((await protocol.HandleAsync("not json", CancellationToken.None))!);
        Assert.Equal(-32700, bad.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    internal static AgentToolContext Context(string permission = ToolPermissions.ReadOnly, Func<Guid, bool>? mayDisclose = null) =>
        new(Guid.NewGuid(), "test", true, permission, mayDisclose ?? (_ => true));

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static DataRow Row(string date, string category, long amount, string currency) => new(new Dictionary<string, object?>
    {
        ["date"] = DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture),
        ["category"] = category,
        ["amount"] = amount,
        ["currency"] = currency
    }, []);

    private sealed class FakeDataset(IReadOnlyList<DataRow> rows) : IDataset
    {
        public string Name => "fake";
        public string Description => "fake";
        public IReadOnlyList<DataField> Fields { get; } =
        [
            new("date", DataField.Date, "", true), new("category", DataField.Text, "", true),
            new("amount", DataField.Money, "", false), new("currency", DataField.Text, "", true)
        ];

        public DatasetSnapshot Snapshot(AgentToolContext context) => new(rows, 0, []);
    }

    private sealed class EchoTool(string tier, string suffix = "") : IAgentTool
    {
        public AgentToolDefinition Definition { get; } = new("echo_" + (suffix.Length > 0 ? suffix : tier), "echo",
            ToolArguments.Schema("""{"type":"object","properties":{"text":{"type":"string"},"repeat":{"type":"integer"}}}"""), tier);

        public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
        {
            ToolArguments args = new(arguments);
            string text = args.Text("text", 100) ?? throw new ArgumentException("'text' is required.");
            return Task.FromResult(AgentToolOutput.Of(new { echo = string.Concat(Enumerable.Repeat(text, args.Integer("repeat", 1, 1, 100_000))) }));
        }
    }

    internal sealed class RecordingAudit : IAgentConversationRepository
    {
        public List<AgentToolCallRecord> Records { get; } = [];
        public void RecordToolCall(AgentToolCallRecord record) => Records.Add(record);
        public IReadOnlyList<AgentBackendProfile> ReadBackends() => throw new NotSupportedException();
        public void SaveBackend(AgentBackendProfile profile) => throw new NotSupportedException();
        public AgentConversation Create(string backendId, string title, string? model = null, string? effort = null,
            string contextPreset = AgentContextPreset.Balanced) => throw new NotSupportedException();
        public AgentConversation? Read(Guid conversationId) => throw new NotSupportedException();
        public AgentConversation SetModel(Guid conversationId, string model, string? effort) => throw new NotSupportedException();
        public AgentConversation SetContext(Guid conversationId, string contextPreset) => throw new NotSupportedException();
        public bool Rename(Guid conversationId, string title) => throw new NotSupportedException();
        public bool Delete(Guid conversationId) => throw new NotSupportedException();
        public IReadOnlyList<AgentConversation> List(int limit) => throw new NotSupportedException();
        public IReadOnlyList<AgentMessage> ReadMessages(Guid conversationId) => throw new NotSupportedException();
        public void Append(Guid conversationId, AgentMessage message) => throw new NotSupportedException();
        public IReadOnlyList<AgentToolCallRecord> ReadToolCalls(Guid conversationId, int limit) => Records;
        public AgentProposal AddProposal(Guid? conversationId, string kind, string payloadJson, string rationale, string status) => throw new NotSupportedException();
        public IReadOnlyList<AgentProposal> ReadProposals(string? status, int limit) => throw new NotSupportedException();
        public AgentProposal? ReadProposal(Guid proposalId) => throw new NotSupportedException();
        public bool SetProposalStatus(Guid proposalId, string expectedStatus, string status) => throw new NotSupportedException();
        public AgentMemory AddMemory(Guid? conversationId, string text, string status) => throw new NotSupportedException();
        public IReadOnlyList<AgentMemory> ReadMemories(string? status, string? search, int limit) => throw new NotSupportedException();
        public bool SetMemoryStatus(Guid memoryId, string status) => throw new NotSupportedException();
        public void SaveArtifact(AgentArtifact artifact) => throw new NotSupportedException();
        public AgentArtifact? ReadArtifact(Guid artifactId) => throw new NotSupportedException();
        public IReadOnlyList<AgentArtifact> ReadArtifacts(int limit) => throw new NotSupportedException();
    }
}
