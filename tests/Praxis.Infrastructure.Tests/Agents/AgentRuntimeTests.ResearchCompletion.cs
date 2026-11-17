using System.Text;
using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Runtime;
using Praxis.Domain.Agents;
using Praxis.Infrastructure.Agents;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Agents;

public sealed partial class AgentRuntimeTests
{
    [Fact]
    public async Task CompactFullHttpBudgetStopsBeforeSendingAndPreservesOwnerMessage()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        AgentBackendProfile profile = Local();
        catalog.Agents.SaveBackend(profile);
        AgentConversation compact = catalog.Agents.Create(profile.BackendId, "compact");
        catalog.Agents.SetContext(compact.ConversationId, AgentContextPreset.Compact);
        CapturingHandler handler = new("""{"choices":[{"message":{"content":"Accepted"}}],"usage":{"prompt_tokens":100,"completion_tokens":2}}""");
        using HttpClient http = new(handler);
        OpenAiCompatibleAgentBackend backend = new(http, new Uri(profile.Endpoint + "/"), profile.Model, null);
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog), new FixedFactory(backend, null), clock);
        string owner = new('é', 10_000);
        List<string> events = [];

        AgentReply stopped = await runtime.RunAsync(compact.ConversationId, owner,
            (kind, _, _) => events.Add(kind), CancellationToken.None, useTools: false);

        Assert.Equal(AgentReply.ContextBudget, stopped.StopReason);
        Assert.Contains("context_limited", events);
        Assert.Null(handler.Body);
        Assert.Empty(stopped.TurnUsage!);
        AgentMessage[] saved = catalog.Agents.ReadMessages(compact.ConversationId).ToArray();
        Assert.Equal(owner, saved[0].Content);
        Assert.Equal(stopped.Text, saved[^1].Content);

        AgentConversation expanded = catalog.Agents.Create(profile.BackendId, "expanded");
        catalog.Agents.SetContext(expanded.ConversationId, AgentContextPreset.Balanced);
        AgentReply succeeded = await runtime.RunAsync(expanded.ConversationId, owner,
            (_, _, _) => { }, CancellationToken.None, useTools: false);

        Assert.Equal(AgentReply.Completed, succeeded.StopReason);
        Assert.NotNull(handler.Body);
        using JsonDocument payload = JsonDocument.Parse(handler.Body);
        Assert.Equal(owner, payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal(100, succeeded.InputTokens);
        Assert.Single(succeeded.TurnUsage!);
    }

    [Fact]
    public async Task BothHttpProvidersRejectOversizedJsonBeforeTransport()
    {
        const string openAiResponse = """{"choices":[{"message":{"content":"OK"}}],"usage":{"prompt_tokens":8,"completion_tokens":2}}""";
        const string anthropicResponse = """{"content":[{"type":"text","text":"OK"}],"usage":{"input_tokens":8,"output_tokens":2}}""";
        CapturingHandler openAiHandler = new(openAiResponse);
        CapturingHandler anthropicHandler = new(anthropicResponse);
        using HttpClient openAiHttp = new(openAiHandler);
        using HttpClient anthropicHttp = new(anthropicHandler);
        IAgentBudgetedBackend openAi = new OpenAiCompatibleAgentBackend(openAiHttp, new Uri("https://example.test/v1/"), "test-model", null);
        IAgentBudgetedBackend anthropic = new AnthropicAgentBackend(anthropicHttp, new Uri("https://example.test/"), "test-model", "test-key");
        AgentMessage[] transcript = [AgentMessage.FromUser("Sample " + new string('é', 2_000))];
        foreach (IAgentBudgetedBackend provider in new[] { openAi, anthropic })
        {
            AgentRequestTooLargeException error = await Assert.ThrowsAsync<AgentRequestTooLargeException>(() =>
                provider.CompleteWithinAsync("System", transcript, [], 128, CancellationToken.None));
            Assert.True(error.RequestBytes > error.MaximumRequestBytes);
            Assert.Equal(128, error.MaximumRequestBytes);
            AgentTurn accepted = await provider.CompleteWithinAsync("System", transcript, [], 100_000, CancellationToken.None);
            Assert.Equal("OK", accepted.Text);
            Assert.True(Assert.IsType<AgentRequestFootprint>(accepted.Request).RequestBytes > 128);
        }

        Assert.NotNull(openAiHandler.Body);
        Assert.NotNull(anthropicHandler.Body);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"\",\"tool_calls\":[{}]}}]}")]
    public async Task MalformedProviderRepliesReportInvalidResponseRatherThanInternalFailure(string response)
    {
        CapturingHandler handler = new(response);
        using HttpClient http = new(handler);
        OpenAiCompatibleAgentBackend backend = new(http, new Uri("https://example.test/v1/"), "test-model", null);

        Praxis.Application.Inference.InferenceException error =
            await Assert.ThrowsAsync<Praxis.Application.Inference.InferenceException>(() =>
                backend.CompleteAsync("System", [AgentMessage.FromUser("Question")], [], CancellationToken.None));

        Assert.Equal("model_invalid_response", error.Code);
        Assert.NotNull(handler.Body);
    }

    [Fact]
    public async Task AutomaticRecallBytesAreReportedWithoutCopyingSourceTextIntoUsageEvents()
    {
        using SqliteCatalog catalog = await SeededAsync();
        AgentBackendProfile profile = Local() with { BackendId = "ollama" };
        catalog.Agents.SaveBackend(profile);
        AgentConversation chat = catalog.Agents.Create(profile.BackendId, "recall footprint");
        SessionBackend session = new(_ => Task.FromResult(new AgentTurn("The lease records payment by check [1].", [], 20, 4)));
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog), new FixedFactory(null, session), clock);
        List<string> metrics = [];

        AgentReply reply = await runtime.RunAsync(chat.ConversationId, "How is rent paid under my lease?",
            (kind, _, detail) => { if (kind == "usage_turn") { metrics.Add(detail!); } },
            CancellationToken.None, useTools: false);

        AgentTurnUsage usage = Assert.Single(reply.TurnUsage!);
        Assert.True(usage.AutomaticRecallBytes > 0);
        Assert.Equal(0, usage.RecapBytes);
        Assert.Null(usage.Request);
        Assert.Equal(20, usage.InputTokens);
        string receipt = Assert.Single(metrics);
        Assert.Contains("automaticRecallBytes", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("Monthly rent is paid by check", receipt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompactOverflowGetsOneSourceGroundedAnswerTurnInsteadOfDiscardingEvidence()
    {
        using SqliteCatalog catalog = await SeededAsync();
        using MemoryStream original = new(Encoding.UTF8.GetBytes(
            new string('p', 2_000) + " Synthetic walking packet example: opcode 0x2716, length 28 bytes. " + new string('p', 8_000)));
        Guid sourceId = (await catalog.Originals.CaptureFileAsync(
            Path.Combine(inbox, "synthetic-overflow.md"), original, original.Length, "synthetic-overflow.md")).SourceId;
        ScriptedBackend backend = new(
            _ => Turn(Call("knowledge_search", """{"query":"lease","limit":1}""")),
            _ => Turn(Call("knowledge_open", JsonSerializer.Serialize(new { sourceId, offset = 0 }))),
            _ => Turn(Call("knowledge_open", JsonSerializer.Serialize(new { sourceId, offset = 2_000 }))),
            transcript =>
            {
                AgentMessage[] evidence = transcript.Where(message => message.Role == AgentRoles.Tool).ToArray();
                Assert.InRange(evidence.Length, 1, 2);
                Assert.All(transcript.SelectMany(message => message.ToolCalls),
                    call => Assert.Equal("knowledge_open", call.Name));
                AgentMessage opened = evidence[^1];
                using JsonDocument receipt = JsonDocument.Parse(opened.Content);
                string excerpt = receipt.RootElement.GetProperty("result").GetProperty("text").GetString() ?? "";
                Assert.Contains("opcode 0x2716, length 28 bytes", excerpt, StringComparison.Ordinal);
                int citation = receipt.RootElement.GetProperty("citations")[0].GetProperty("n").GetInt32();
                Assert.Contains(transcript, message => message.ToolCalls.Count == 1 &&
                    message.ToolCalls[0].CallId == opened.ToolCallId);
                return new AgentTurn($"The synthetic packet is 0x2716 and 28 bytes [{citation}].", [], 10, 5);
            })
        {
            AllowToollessTurn = true
        };
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        catalog.Agents.SetContext(conversation.ConversationId, AgentContextPreset.Compact);
        List<string> events = [];

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId,
            "Read the synthetic walking packet and cite its opcode and length",
            (kind, _, _) => events.Add(kind), CancellationToken.None,
            new AgentRunLimits(CompactAfterCharacters: 5_000));

        Assert.Equal(AgentReply.Completed, reply.StopReason);
        Assert.True(reply.Check.Clean);
        Assert.Equal(1, reply.SearchCalls);
        Assert.Equal(2, reply.SourceReads);
        Assert.Equal(4, reply.TurnUsage!.Count);
        Assert.Equal(0, backend.ToolCounts[^1]);
        Assert.Contains("progress_limited", events);
        Assert.Contains("Do not call tools.", backend.SystemPrompts[^1], StringComparison.Ordinal);
        Assert.Contains("Cite only the opened passages present in this request.", backend.SystemPrompts[^1], StringComparison.Ordinal);
        Assert.Equal(3, reply.ToolCalls.Count);
        Assert.Equal(reply.Text, catalog.Agents.ReadMessages(conversation.ConversationId)[^1].Content);
    }

    [Theory]
    [InlineData(true, 5_000)]
    [InlineData(false, 5_000)]
    [InlineData(true, 7_300)]
    [InlineData(false, 7_300)]
    public async Task CompactSourceOnlyCorrectionIsVerifiedOnceWithoutFurtherToolCalls(bool corrected, int historyBudget)
    {
        using SqliteCatalog catalog = await SeededAsync();
        using MemoryStream original = new(Encoding.UTF8.GetBytes(
            new string('p', 2_000) + " Synthetic packet evidence: opcode 0x2716, length 28 bytes. " + new string('p', 8_000)));
        Guid sourceId = (await catalog.Originals.CaptureFileAsync(
            Path.Combine(inbox, "synthetic-repair.md"), original, original.Length, "synthetic-repair.md")).SourceId;
        int citation = 0;
        ScriptedBackend backend = new(
            _ => Turn(Call("knowledge_search", """{"query":"lease","limit":1}""")),
            _ => Turn(Call("knowledge_open", JsonSerializer.Serialize(new { sourceId, offset = 0 }))),
            _ => Turn(Call("knowledge_open", JsonSerializer.Serialize(new { sourceId, offset = 2_000 }))),
            transcript =>
            {
                AgentMessage opened = transcript.Last(message => message.Role == AgentRoles.Tool);
                using JsonDocument receipt = JsonDocument.Parse(opened.Content);
                Assert.Contains("opcode 0x2716, length 28 bytes", receipt.RootElement.GetProperty("result")
                    .GetProperty("text").GetString() ?? "", StringComparison.Ordinal);
                citation = receipt.RootElement.GetProperty("citations")[0].GetProperty("n").GetInt32();
                return new AgentTurn($"The synthetic packet opcode is 0x42D [{citation}].", [], 10, 4);
            },
            _ => new AgentTurn(corrected
                ? $"The synthetic packet opcode is 0x2716 and the size is 28 bytes [{citation}]."
                : $"The synthetic packet opcode is 0x42D [{citation}].", [], 10, 4))
        {
            AllowToollessTurn = true
        };
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        catalog.Agents.SetContext(conversation.ConversationId, AgentContextPreset.Compact);
        List<string> events = [];

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Find synthetic packet opcode and size",
            (kind, _, _) => events.Add(kind), CancellationToken.None,
            new AgentRunLimits(CompactAfterCharacters: historyBudget));

        Assert.Equal(corrected ? AgentReply.Completed : AgentReply.EvidenceInsufficient, reply.StopReason);
        Assert.Equal(corrected, reply.Check.Clean);
        Assert.True(reply.Check.Repaired);
        Assert.Equal(5, reply.TurnUsage!.Count);
        Assert.Equal(1, reply.SearchCalls);
        Assert.Equal(2, reply.SourceReads);
        Assert.All(backend.ToolCounts.TakeLast(2), count => Assert.Equal(0, count));
        Assert.Contains("repairing", events);
        if (!corrected)
        {
            Assert.DoesNotContain("repeated source reads", reply.Text, StringComparison.Ordinal);
        }
        Assert.Contains("Remove unsupported values", backend.SystemPrompts[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("0x42D", reply.Text, StringComparison.Ordinal);
        if (!corrected)
        {
            Assert.Contains("0x42D", reply.Check.UnsupportedFigures);
        }

        Assert.Equal(reply.Text, catalog.Agents.ReadMessages(conversation.ConversationId)[^1].Content);
    }

    [Theory]
    [InlineData(3_600, 1)]
    [InlineData(7_300, 2)]
    public async Task CompactEvidenceSynthesisPreservesRankedPassagesAndStopsBeforeAnotherLargeRead(
        int historyBudget, int expectedForwardedReads)
    {
        using SqliteCatalog catalog = await SeededAsync();
        using MemoryStream relevant = new(Encoding.UTF8.GetBytes(
            "Unique packet evidence: opcode 0x2716 and length 28 bytes. " + new string('a', 7_000)));
        using MemoryStream unrelated = new(Encoding.UTF8.GetBytes(
            "Unrelated notes. " + new string('z', 7_000)));
        Guid relevantId = (await catalog.Originals.CaptureFileAsync(
            Path.Combine(inbox, "ranked-packet.md"), relevant, relevant.Length, "ranked-packet.md")).SourceId;
        Guid unrelatedId = (await catalog.Originals.CaptureFileAsync(
            Path.Combine(inbox, "unrelated-packet.md"), unrelated, unrelated.Length, "unrelated-packet.md")).SourceId;
        await ProcessingDriver.RunIntakeAsync(catalog,
            CatalogProcessing.CreateHandlers(catalog, new ScriptedModelFactory(), clock));
        ScriptedBackend backend = new(
            _ => Turn(Call("knowledge_search", """{"query":"Unique packet evidence","limit":1}""")),
            transcript =>
            {
                Assert.Contains(relevantId.ToString(), transcript.Last(message => message.Role == AgentRoles.Tool).Content,
                    StringComparison.OrdinalIgnoreCase);
                return Turn(
                    Call("knowledge_open", JsonSerializer.Serialize(new { sourceId = relevantId })),
                    Call("knowledge_open", JsonSerializer.Serialize(new { sourceId = unrelatedId })));
            },
            transcript =>
            {
                HashSet<string> readCallIds = transcript.SelectMany(message => message.ToolCalls)
                    .Where(call => call.Name == "knowledge_open")
                    .Select(call => call.CallId).ToHashSet(StringComparer.Ordinal);
                AgentMessage[] evidence = transcript.Where(message => message.Role == AgentRoles.Tool &&
                    message.ToolCallId is { } id && readCallIds.Contains(id)).ToArray();
                Assert.Equal(expectedForwardedReads, evidence.Length);
                Assert.Contains(evidence, message => message.Content.Contains("opcode 0x2716", StringComparison.Ordinal));
                if (expectedForwardedReads == 1)
                {
                    Assert.DoesNotContain(evidence, message => message.Content.Contains("Unrelated notes", StringComparison.Ordinal));
                }

                AgentMessage opened = Assert.Single(evidence, message => message.Content.Contains("opcode 0x2716", StringComparison.Ordinal));
                using JsonDocument receipt = JsonDocument.Parse(opened.Content);
                int citation = receipt.RootElement.GetProperty("citations")[0].GetProperty("n").GetInt32();
                Assert.Contains(transcript, message => message.ToolCalls.Any(call => call.CallId == opened.ToolCallId));
                return new AgentTurn($"The documented synthetic opcode is 0x2716 [{citation}].", [], 10, 5);
            })
        {
            AllowToollessTurn = true
        };
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        catalog.Agents.SetContext(conversation.ConversationId, AgentContextPreset.Compact);
        List<string> events = [];

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Identify the unique packet opcode",
            (kind, _, _) => events.Add(kind), CancellationToken.None,
            new AgentRunLimits(CompactAfterCharacters: historyBudget));

        Assert.Equal(AgentReply.Completed, reply.StopReason);
        Assert.True(reply.Check.Clean);
        Assert.Equal(1, reply.SearchCalls);
        Assert.Equal(2, reply.SourceReads);
        Assert.Equal(0, backend.ToolCounts[^1]);
        Assert.Equal(3, reply.TurnUsage!.Count);
        Assert.Contains("progress_limited", events);
    }

    [Fact]
    public async Task RepeatedSixThousandCharacterSourcePagesStopWithPairedToolTraffic()
    {
        using SqliteCatalog catalog = await SeededAsync();
        using MemoryStream original = new(Encoding.UTF8.GetBytes("# Packet fixture\n" + new string('p', 210_000)));
        Guid sourceId = (await catalog.Originals.CaptureFileAsync(
            Path.Combine(inbox, "synthetic-packet.md"), original, original.Length, "synthetic-packet.md")).SourceId;
        int pages = 0;
        ScriptedBackend backend = new((_, _) => Task.FromResult(Turn(Call("knowledge_open",
            JsonSerializer.Serialize(new { sourceId, offset = pages++ * 6_000 })))))
        {
            AllowToollessTurn = true
        };
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        catalog.Agents.SetContext(conversation.ConversationId, AgentContextPreset.Expanded);
        List<string> events = [];

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Read the synthetic packet fixture",
            (kind, _, _) => events.Add(kind), CancellationToken.None,
            new AgentRunLimits(MaximumSteps: 16, MaximumInputTokens: 100_000));

        Assert.Equal(AgentReply.NoProgress, reply.StopReason);
        Assert.Contains("progress_limited", events);
        Assert.InRange(reply.SourceReads, 2, 15);
        Assert.Equal(0, backend.ToolCounts[^1]);
        Assert.Equal(reply.SourceReads, reply.ToolCalls.Count);
        AgentMessage[] transcript = catalog.Agents.ReadMessages(conversation.ConversationId).ToArray();
        Assert.Equal(reply.SourceReads, transcript.Count(message => message.Role == AgentRoles.Tool));
        Assert.Equal(reply.SourceReads, transcript.Count(message => message.Role == AgentRoles.Assistant && message.ToolCalls.Count > 0));
        Assert.Equal(reply.Text, transcript[^1].Content);
        Assert.DoesNotContain(transcript, message => message.Role == AgentRoles.Tool && message.Content.Length == 0);
    }

    [Fact]
    public async Task SearchCitationCannotSubstituteForAnOpenedPacketSource()
    {
        using SqliteCatalog catalog = await SeededAsync();
        using MemoryStream original = new(Encoding.UTF8.GetBytes("Synthetic walking packet example: opcode 0x2716, length 28 bytes."));
        Guid sourceId = (await catalog.Originals.CaptureFileAsync(
            Path.Combine(inbox, "synthetic-walk-packet.md"), original, original.Length, "synthetic-walk-packet.md")).SourceId;
        int searchCitation = 0;
        int openCitation = 0;
        ScriptedBackend backend = new(
            _ => Turn(Call("knowledge_search", """{"query":"lease","limit":1}""")),
            transcript =>
            {
                using JsonDocument search = JsonDocument.Parse(transcript.Last(message => message.Role == AgentRoles.Tool).Content);
                searchCitation = search.RootElement.GetProperty("citations")[0].GetProperty("n").GetInt32();
                return Turn(Call("knowledge_open", JsonSerializer.Serialize(new { sourceId })));
            },
            transcript =>
            {
                using JsonDocument source = JsonDocument.Parse(transcript.Last(message => message.Role == AgentRoles.Tool).Content);
                openCitation = source.RootElement.GetProperty("citations")[0].GetProperty("n").GetInt32();
                Assert.NotEqual(searchCitation, openCitation);
                return new AgentTurn($"The example uses opcode 0x2716 and 28 bytes [{searchCitation}].", [], 10, 4);
            },
            _ => new AgentTurn($"The example uses opcode 0x2716 and 28 bytes [{openCitation}].", [], 10, 4));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        List<string> events = [];

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId,
            "Read the synthetic walking packet and report its opcode and length",
            (kind, _, _) => events.Add(kind), CancellationToken.None);

        Assert.Equal(AgentReply.Completed, reply.StopReason);
        Assert.True(reply.Check.Clean);
        Assert.True(reply.Check.Repaired);
        Assert.Contains("repairing", events);
        Assert.Contains($"[{openCitation}]", reply.Text, StringComparison.Ordinal);
        Assert.Equal(1, reply.SearchCalls);
        Assert.Equal(1, reply.SourceReads);
        Assert.Equal(4, reply.TurnUsage!.Count);
    }

    [Fact]
    public async Task DisclosureRevokedBetweenToolAndModelTurnStopsWithoutForwardingOldEvidence()
    {
        using SqliteCatalog catalog = await SeededAsync();
        AgentBackendProfile remote = Local() with { BackendId = "remote", Endpoint = "https://example.test/v1" };
        Guid source = Assert.Single(catalog.Search.Query("lease", 10)).SourceId;
        catalog.Policy.ApplyChange(new Praxis.Domain.Policy.PolicyChange(Mode: Praxis.Domain.Policy.DisclosureModes.AllowAll),
            Praxis.Domain.Policy.PolicyActors.Owner);
        catalog.Policy.SaveCategory(new Praxis.Domain.Policy.SourceCategory(source, Praxis.Domain.Policy.DataCategories.Documents,
            Praxis.Domain.Policy.SourceCategory.OwnerOrigin, false));
        ScriptedBackend backend = new(_ => Turn(Call("knowledge_search", """{"query":"lease","limit":1}""")));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, remote, backend);

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Find my lease",
            (kind, tool, _) =>
            {
                if (kind == "tool_finished" && tool == "knowledge_search")
                {
                    catalog.Policy.SaveCategory(new Praxis.Domain.Policy.SourceCategory(source, Praxis.Domain.Policy.DataCategories.Documents,
                        Praxis.Domain.Policy.SourceCategory.OwnerOrigin, true));
                }
            }, CancellationToken.None);

        Assert.Equal(AgentReply.DisclosureChanged, reply.StopReason);
        Assert.Empty(reply.Citations);
        Assert.Single(backend.SystemPrompts);
        Assert.Equal(1, reply.SearchCalls);
        Assert.Equal(reply.Text, catalog.Agents.ReadMessages(conversation.ConversationId)[^1].Content);
        Assert.DoesNotContain("Monthly rent", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AggregateResultWithoutCitationRejectsAnInventedCitationThenRepairs()
    {
        using SqliteCatalog catalog = await SeededAsync();
        ScriptedBackend backend = new(
            _ => Turn(Call("knowledge_search", """{"query":"rent","limit":1}"""),
                Call("data_query", """{"dataset":"transactions","filters":[{"field":"direction","op":"eq","values":["outflow"]}],"metrics":[{"function":"sum","field":"amount"}]}""")),
            transcript =>
            {
                using JsonDocument aggregate = JsonDocument.Parse(transcript.Last(message => message.Role == AgentRoles.Tool).Content);
                Assert.Empty(aggregate.RootElement.GetProperty("citations").EnumerateArray());
                return new AgentTurn("The lease is in lease.md [1]. October outflows were $1,270.20 [2].", [], 10, 4);
            },
            _ => new AgentTurn("The lease is in lease.md [1]. October outflows were $1,270.20, computed by data_query.", [], 10, 4));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        List<string> events = [];

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId,
            "What did I spend in October and where is my lease?",
            (kind, _, _) => events.Add(kind), CancellationToken.None);

        Assert.Equal(AgentReply.Completed, reply.StopReason);
        Assert.True(reply.Check.Clean);
        Assert.True(reply.Check.Repaired);
        Assert.Contains("repairing", events);
        Assert.Single(reply.Citations);
        Assert.Equal(2, reply.ToolCalls.Count);
        Assert.DoesNotContain("[2]", reply.Text, StringComparison.Ordinal);
        Assert.Contains("valid [n]", backend.SystemPrompts[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedUnsupportedPacketFigureHasExplicitEvidenceInsufficientStop()
    {
        using SqliteCatalog catalog = await SeededAsync();
        ScriptedBackend backend = new(
            _ => Turn(Call("knowledge_search", """{"query":"lease","limit":1}""")),
            _ => new AgentTurn("The packet opcode is 0xDEADBEEF [1].", [], 10, 4),
            _ => new AgentTurn("The packet opcode is 0xDEADBEEF [1].", [], 10, 4));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Find documented packet opcode",
            (_, _, _) => { }, CancellationToken.None);

        Assert.Equal(AgentReply.EvidenceInsufficient, reply.StopReason);
        Assert.False(reply.Check.Clean);
        Assert.Contains("0xDEADBEEF", reply.Check.UnsupportedFigures);
        Assert.Equal(3, reply.TurnUsage!.Count);
        Assert.Contains("couldn’t verify", reply.Text, StringComparison.Ordinal);
        Assert.Equal(reply.Text, catalog.Agents.ReadMessages(conversation.ConversationId)[^1].Content);
    }

    [Fact]
    public void OpenedSourceMetadataCannotProveAClaimAboutPacketSize()
    {
        Guid sourceId = Guid.NewGuid();
        string raw = JsonSerializer.Serialize(new
        {
            sourceId,
            revision = 10005,
            offset = 10005,
            totalLength = 10005,
            text = "The packet size is not documented here."
        });
        string wrapped = JsonSerializer.Serialize(new
        {
            result = new { sourceId, revision = 10005, offset = 10005, text = "The packet size is not documented here." },
            citations = new[] { new { n = 1 } }
        });

        foreach (string receipt in new[] { raw, wrapped })
        {
            AnswerCheck check = AnswerVerifier.Check("The packet size is 10005 bytes [1].",
                [receipt], "What size is the packet?", true, false, 1, new HashSet<int> { 1 });
            Assert.Contains("10005", check.UnsupportedFigures);
            Assert.False(check.Clean);
        }

        AnswerCheck supported = AnswerVerifier.Check("The packet size is 10005 bytes [1].",
            [JsonSerializer.Serialize(new { sourceId, text = "The packet size is 10005 bytes." })],
            "What size is the packet?", true, false, 1, new HashSet<int> { 1 });
        Assert.True(supported.Clean);
    }

    [Theory]
    [InlineData("The example uses opcode 0x2716 and 28 bytes [2].", false, false)]
    [InlineData("The example uses opcode 0xDEAD and 28 bytes [2].", true, false)]
    [InlineData("The example uses opcode 0x2716 and 28 bytes [1].", false, true)]
    [InlineData("The example uses opcode 0x2716 and 28 bytes [99].", false, true)]
    [InlineData("The example uses opcode 0x2716 and 28 bytes.", false, true)]
    public void TechnicalFiguresAndOpenedCitationsAreValidatedWithoutTreatingHexAsDecimal(string answer, bool unsupportedHex, bool citationFailure)
    {
        AnswerCheck result = AnswerVerifier.Check(answer,
            ["Synthetic fixture: opcode 0x2716, size 28 bytes."], "Report the packet format", true, false, 2, new HashSet<int> { 2 });

        Assert.Equal(unsupportedHex, result.UnsupportedFigures.Contains("0xDEAD", StringComparer.OrdinalIgnoreCase));
        Assert.Equal(citationFailure, result.MissingCitations);
    }
}
