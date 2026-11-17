using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Praxis.Application.Agents;
using Praxis.Domain.Agents;

namespace Praxis.Infrastructure.Agents;

/// <summary>
/// Tool use over the Anthropic Messages API. Consecutive tool results are grouped into one user
/// turn and roles are kept alternating, as the API requires.
/// </summary>
public sealed class AnthropicAgentBackend(HttpClient http, Uri endpoint, string model, string apiKey, int maxTokens = 8_192) : IAgentBackend
{
    public async Task<AgentTurn> CompleteAsync(string systemPrompt, IReadOnlyList<AgentMessage> transcript, IReadOnlyList<AgentToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        JsonArray messages = [];
        foreach (AgentMessage message in transcript)
        {
            (string role, JsonNode block) = message.Role switch
            {
                AgentRoles.Tool => ("user", new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = message.ToolCallId, ["content"] = message.Content }),
                AgentRoles.Summary => ("user", Text("Summary of the earlier conversation:\n" + message.Content)),
                AgentRoles.Assistant => ("assistant", Text(message.Content)),
                _ => ("user", Text(message.Content))
            };
            JsonArray blocks = Append(messages, role);
            if (message.Role != AgentRoles.Assistant || message.Content.Length > 0)
            {
                blocks.Add(block);
            }

            if (message.Role == AgentRoles.Assistant)
            {
                foreach (AgentToolCall call in message.ToolCalls)
                {
                    blocks.Add(new JsonObject { ["type"] = "tool_use", ["id"] = call.CallId, ["name"] = call.Name, ["input"] = JsonNode.Parse(Object(call.ArgumentsJson)) });
                }
            }
        }

        JsonObject body = new() { ["model"] = model, ["max_tokens"] = maxTokens, ["system"] = systemPrompt, ["messages"] = messages };
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray(tools.Select(tool => (JsonNode)new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = JsonNode.Parse(tool.InputSchema.GetRawText())
            }).ToArray());
        }

        string payload = body.ToJsonString();
        AgentRequestFootprint footprint = AgentHttpRequestFootprint.Measure(payload, body, systemPrompt, transcript, tools.Count);
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(endpoint, "v1/messages"))
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        using HttpResponseMessage response = await HttpAgentErrors.SendAsync(() => http.SendAsync(request, cancellationToken), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw HttpAgentErrors.From(response.StatusCode);
        }

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        StringBuilder text = new();
        List<AgentToolCall> calls = [];
        foreach (JsonElement block in document.RootElement.GetProperty("content").EnumerateArray())
        {
            switch (block.GetProperty("type").GetString())
            {
                case "text":
                    text.Append(block.GetProperty("text").GetString());
                    break;
                case "tool_use":
                    calls.Add(new AgentToolCall(block.GetProperty("id").GetString()!, block.GetProperty("name").GetString()!, block.GetProperty("input").GetRawText()));
                    break;
            }
        }

        JsonElement usage = document.RootElement.TryGetProperty("usage", out JsonElement used) ? used : default;
        return new AgentTurn(text.ToString(), calls, Tokens(usage, "input_tokens") + Tokens(usage, "cache_read_input_tokens"), Tokens(usage, "output_tokens"), footprint);
    }

    private static JsonArray Append(JsonArray messages, string role)
    {
        if (messages.Count > 0 && messages[^1]!["role"]!.GetValue<string>() == role)
        {
            return messages[^1]!["content"]!.AsArray();
        }

        JsonArray content = [];
        messages.Add(new JsonObject { ["role"] = role, ["content"] = content });
        return content;
    }

    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text.Length == 0 ? "(no text)" : text };

    private static string Object(string json)
    {
        try
        {
            return JsonNode.Parse(json) is JsonObject ? json : "{}";
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    private static int Tokens(JsonElement usage, string name) =>
        usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int tokens) ? tokens : 0;
}
