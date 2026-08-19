using System.Text.Json;

using LR.Core.Models;
using LR.Core.Models.OpenAI;

namespace LR.Providers;

/// <summary>
/// Parses llama.cpp's native Anthropic-compatible (/v1/messages) HTTP responses into
/// RouteResponse objects. Mirrors <see cref="LlamaCppResponseParser"/>, which handles the
/// OpenAI-shaped (/v1/chat/completions) responses used by every other protocol.
/// </summary>
public static class LlamaCppClaudeResponseParser
{
    /// <summary>
    /// Parses a non-streaming Anthropic Messages API response into an existing RouteResponse.
    /// </summary>
    public static void ParseRouteResponseInto(JsonElement root, RouteResponse response)
    {
        // content is an array of blocks; concatenate "text" blocks and separately collect
        // "thinking" blocks. Other block types (tool_use, etc.) aren't surfaced yet — the Claude
        // protocol handler doesn't build tool_use content blocks on the way back to the client.
        if (root.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
        {
            var text = new System.Text.StringBuilder();
            var thinking = new System.Text.StringBuilder();
            List<ChatToolCall>? toolCalls = null;
            foreach (var block in content.EnumerateArray())
            {
                string? blockType = block.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;
                if (blockType == "text" && block.TryGetProperty("text", out JsonElement textEl))
                {
                    text.Append(textEl.GetString());
                }
                else if (blockType == "thinking" && block.TryGetProperty("thinking", out JsonElement thinkingEl))
                {
                    thinking.Append(thinkingEl.GetString());
                }
                else if (blockType == "tool_use")
                {
                    string id = block.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                    string name = block.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                    string argumentsJson = block.TryGetProperty("input", out JsonElement inputEl) ? inputEl.GetRawText() : "{}";

                    (toolCalls ??= new List<ChatToolCall>()).Add(new ChatToolCall
                    {
                        Id = id,
                        Type = "function",
                        Function = new ChatToolCallFunction { Name = name, Arguments = argumentsJson }
                    });
                }
            }
            response.Payload = text.ToString();
            response.ReasoningContent = thinking.Length > 0 ? thinking.ToString() : null;
            response.ToolCalls = toolCalls;
        }

        response.FinishReason = root.TryGetProperty("stop_reason", out JsonElement stopReason) && stopReason.ValueKind == JsonValueKind.String
            ? stopReason.GetString()
            : null;

        if (root.TryGetProperty("usage", out JsonElement usage))
        {
            response.PromptTokensProcessed = GetInt32(usage, "input_tokens") ?? 0;
            response.GeneratedTokenCount = GetInt32(usage, "output_tokens") ?? 0;
        }

        // llama.cpp includes the same "timings" object on its Anthropic-shaped responses as it
        // does on the OpenAI ones — pick it up the same way if present.
        if (root.TryGetProperty("timings", out JsonElement timings))
        {
            response.PromptProcessingMs = GetDouble(timings, "prompt_ms") ?? 0;
            response.GenerationMs = GetDouble(timings, "predicted_ms") ?? GetDouble(timings, "generation_ms") ?? 0;
        }

        response.TotalLatencyMs = response.PromptProcessingMs + response.GenerationMs;
    }

    private static int? GetInt32(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Number)
            return value.GetInt32();
        return null;
    }

    private static double? GetDouble(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Number)
            return value.GetDouble();
        return null;
    }
}
