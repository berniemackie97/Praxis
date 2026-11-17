using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Praxis.Application.Agents;
using Praxis.Application.Inference;
using Praxis.Domain.Agents;
using Praxis.Domain.Models;
using Praxis.Infrastructure.Inference.Ollama;

namespace Praxis.Infrastructure.Agents;

/// <summary>
/// Tool calling over the OpenAI-compatible chat completions API: OpenAI, DeepSeek, OpenRouter,
/// Gemini's compatible endpoint, and local servers such as Ollama or LM Studio (<c>/v1</c>).
/// </summary>
public sealed class OpenAiCompatibleAgentBackend(HttpClient http, Uri endpoint, string model, string? apiKey, bool isLocalOllama = false) : IAgentBackend
{
    public async ValueTask<bool> SupportsToolsAsync(CancellationToken cancellationToken)
    {
        if (!isLocalOllama)
        {
            return true;
        }

        using OllamaRuntime runtime = new(endpoint.GetLeftPart(UriPartial.Authority), TimeSpan.FromSeconds(2));
        await runtime.RequireLocalModelAsync(model, cancellationToken);
        LocalModelCapabilities? capabilities;
        try
        {
            capabilities = await runtime.ReadCapabilitiesAsync(model, cancellationToken);
        }
        catch (InferenceException)
        {
            // An older runtime may not describe capabilities. The chat request still reports provider failures.
            capabilities = null;
        }

        if (capabilities?.Chat == false || (capabilities is null && model.Contains("embed", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InferenceException(InferenceFailure.ChatNotSupported, "This model does not support chat. Choose a chat model.");
        }

        // Older Ollama versions may not report metadata for the standard Gemma 2/3 tags.
        string family = model.Split(':', 2)[0];
        return capabilities?.Tools ?? (family is not ("gemma2" or "gemma3" or "library/gemma2" or "library/gemma3"));
    }

    public async Task<AgentTurn> CompleteAsync(string systemPrompt, IReadOnlyList<AgentMessage> transcript, IReadOnlyList<AgentToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        JsonArray messages = [new JsonObject { ["role"] = "system", ["content"] = systemPrompt }];
        foreach (AgentMessage message in transcript)
        {
            messages.Add(message.Role switch
            {
                AgentRoles.Tool => new JsonObject { ["role"] = "tool", ["tool_call_id"] = message.ToolCallId, ["content"] = message.Content },
                AgentRoles.Assistant => Assistant(message),
                AgentRoles.Summary => new JsonObject { ["role"] = "user", ["content"] = "Summary of the earlier conversation:\n" + message.Content },
                _ => new JsonObject { ["role"] = "user", ["content"] = message.Content }
            });
        }

        JsonObject body = new() { ["model"] = model, ["messages"] = messages };
        if (isLocalOllama)
        {
            body["reasoning_effort"] = "none";
        }

        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray(tools.Select(tool => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description, ["parameters"] = JsonNode.Parse(tool.InputSchema.GetRawText()) }
            }).ToArray());
            body["tool_choice"] = "auto";
        }

        string payload = body.ToJsonString();
        AgentRequestFootprint footprint = AgentHttpRequestFootprint.Measure(payload, body, systemPrompt, transcript, tools.Count);
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(endpoint, "chat/completions"))
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        if (apiKey is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using HttpResponseMessage response = await HttpAgentErrors.SendAsync(() => http.SendAsync(request, cancellationToken), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw HttpAgentErrors.From(response.StatusCode);
        }

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("choices", out JsonElement choices) || choices.GetArrayLength() == 0)
        {
            throw new InferenceException(InferenceFailure.InvalidResponse, "The provider returned no choices.");
        }

        JsonElement reply = choices[0].GetProperty("message");
        string text = reply.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String ? content.GetString()! : "";
        List<AgentToolCall> calls = [];
        if (reply.TryGetProperty("tool_calls", out JsonElement toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement call in toolCalls.EnumerateArray().Take(16))
            {
                JsonElement function = call.GetProperty("function");
                calls.Add(new AgentToolCall(call.TryGetProperty("id", out JsonElement id) ? id.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N"),
                    function.GetProperty("name").GetString() ?? "", function.TryGetProperty("arguments", out JsonElement arguments)
                        ? arguments.ValueKind == JsonValueKind.String ? arguments.GetString() ?? "{}" : arguments.GetRawText() : "{}"));
            }
        }

        JsonElement usage = root.TryGetProperty("usage", out JsonElement used) ? used : default;
        return new AgentTurn(isLocalOllama ? VisibleOllamaAnswer(text, calls.Count != 0) : text,
            calls, Tokens(usage, "prompt_tokens"), Tokens(usage, "completion_tokens"), footprint);
    }

    private static string VisibleOllamaAnswer(string reply, bool hasToolCalls)
    {
        if (hasToolCalls)
        {
            return "";
        }

        // Some local templates put analysis into the visible content field, occasionally without
        // the opening delimiter. Never present that text or treat printed JSON as a tool call.
        const string close = "</think>";
        int boundary = reply.IndexOf(close, StringComparison.OrdinalIgnoreCase);
        string text = boundary >= 0 ? reply[(boundary + close.Length)..].Trim() : reply.Trim();
        if (text.StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        if (text.StartsWith('{'))
        {
            try
            {
                using JsonDocument printed = JsonDocument.Parse(text);
                if (printed.RootElement.TryGetProperty("name", out _) &&
                    printed.RootElement.TryGetProperty("arguments", out _))
                {
                    return "";
                }
            }
            catch (JsonException)
            {
                // Ordinary prose is not required to be JSON.
            }
        }

        return text;
    }

    private static JsonObject Assistant(AgentMessage message)
    {
        JsonObject node = new() { ["role"] = "assistant", ["content"] = message.Content };
        if (message.ToolCalls.Count > 0)
        {
            node["tool_calls"] = new JsonArray(message.ToolCalls.Select(call => (JsonNode)new JsonObject
            {
                ["id"] = call.CallId,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.ArgumentsJson }
            }).ToArray());
        }

        return node;
    }

    private static int Tokens(JsonElement usage, string name) =>
        usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int tokens) ? tokens : 0;
}
