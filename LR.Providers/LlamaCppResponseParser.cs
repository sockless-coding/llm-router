using System.Text.Json;

using LR.Core.Models;
using LR.Core.Models.OpenAI;

namespace LR.Providers;

/// <summary>
/// Parses llama.cpp OpenAI-compatible HTTP responses into RouteResponse objects.
/// All methods are pure static functions — no instance state required.
/// </summary>
public static class LlamaCppResponseParser
{
    /// <summary>
    /// Parses a non-streaming llama.cpp response into a new RouteResponse.
    /// </summary>
    public static RouteResponse ParseRouteResponse(JsonElement root)
    {
        var response = new RouteResponse();
        ParseRouteResponseInto(root, response);
        return response;
    }

    /// <summary>
    /// Parses llama.cpp OpenAI-compatible JSON response into an existing RouteResponse.
    /// Used when we pre-allocate the RouteResponse for stdout timing correlation.
    /// </summary>
    public static void ParseRouteResponseInto(JsonElement root, RouteResponse response)
    {
        // Extract content from choices[0].message.content
        if (root.TryGetProperty("choices", out JsonElement choices) && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("message", out JsonElement message))
            {
                response.Payload = message.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String
                    ? content.GetString() ?? string.Empty
                    : string.Empty;

                response.ReasoningContent = message.TryGetProperty("reasoning_content", out JsonElement reasoning) && reasoning.ValueKind == JsonValueKind.String
                    ? reasoning.GetString()
                    : null;

                if (message.TryGetProperty("tool_calls", out JsonElement toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    response.ToolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(toolCalls.GetRawText());
                }
            }

            response.FinishReason = firstChoice.TryGetProperty("finish_reason", out JsonElement finishReason) && finishReason.ValueKind == JsonValueKind.String
                ? finishReason.GetString()
                : null;
        }

        // Extract usage data
        if (root.TryGetProperty("usage", out JsonElement usage))
        {
            response.PromptTokensProcessed = GetInt32(usage, "prompt_tokens") ?? 0;
            response.GeneratedTokenCount = GetInt32(usage, "completion_tokens") ?? 0;
        }

        // Extract timing data (llama.cpp may include these in the top-level or under usage)
        if (root.TryGetProperty("timing", out JsonElement timing))
        {
            response.PromptProcessingMs = GetDouble(timing, "prompt_ms") ?? 0;
            response.GenerationMs = GetDouble(timing, "predicted_ms") ?? 0;
        }

        // Some llama.cpp versions put timings in usage
        if (root.TryGetProperty("usage", out JsonElement usageTiming))
        {
            if (response.PromptProcessingMs == 0)
                response.PromptProcessingMs = GetDouble(usageTiming, "prompt_ms") ?? 0;
            if (response.GenerationMs == 0)
                response.GenerationMs = GetDouble(usageTiming, "predicted_ms") ?? GetDouble(usageTiming, "time_generation_ms") ?? 0;
        }

        // First token latency from timing if available
        if (root.TryGetProperty("timing", out JsonElement timingFirst))
        {
            response.FirstTokenLatencyMs = GetDouble(timingFirst, "predicted_n_first_token_ms") ?? 0;
        }

        response.TotalLatencyMs = response.PromptProcessingMs + response.GenerationMs;
    }

    /// <summary>
    /// Builds a RouteResponse from streaming metadata.
    /// </summary>
    public static RouteResponse BuildRouteResponseFromStream(string accumulatedText, JsonElement root)
    {
        var response = new RouteResponse { Payload = accumulatedText };
        BuildRouteResponseFromStreamInto(accumulatedText, root, response);
        return response;
    }

    /// <summary>
    /// Populates an existing RouteResponse from streaming metadata.
    /// Used when we pre-allocate the RouteResponse for stdout timing correlation.
    /// </summary>
    public static void BuildRouteResponseFromStreamInto(string accumulatedText, JsonElement root, RouteResponse response)
    {
        response.Payload = accumulatedText;

        if (root.TryGetProperty("usage", out JsonElement usage))
        {
            response.PromptTokensProcessed = GetInt32(usage, "prompt_tokens") ?? 0;
            response.GeneratedTokenCount = GetInt32(usage, "completion_tokens") ?? 0;

            // Some llama.cpp versions put timing data in the usage object of streaming responses
            if (response.PromptProcessingMs == 0)
                response.PromptProcessingMs = GetDouble(usage, "prompt_ms") ?? 0;
            if (response.GenerationMs == 0)
                response.GenerationMs = GetDouble(usage, "predicted_ms") ?? GetDouble(usage, "time_generation_ms") ?? 0;
        }

        // Extract full timings object from top-level "timings" property (llama-cpp-server format)
        if (root.TryGetProperty("timings", out JsonElement timingsJson))
        {
            var parsedTimings = ParseLlamaCppTimings(timingsJson);
            response.BackendTimings = parsedTimings;

            // Also populate scalar properties from the timings object as fallback
            if (parsedTimings.PromptMs.HasValue && response.PromptProcessingMs == 0)
                response.PromptProcessingMs = parsedTimings.PromptMs.Value;
            var genMs = parsedTimings.GenerationMs ?? parsedTimings.PredictedMs;
            if (genMs.HasValue && response.GenerationMs == 0)
                response.GenerationMs = genMs.Value;
        }

        // Extract timing data from top-level "timing" object (alternative key)
        if (response.BackendTimings == null && root.TryGetProperty("timing", out JsonElement timingJson))
        {
            var parsedTiming = ParseLlamaCppTimings(timingJson);
            response.BackendTimings = parsedTiming;

            var promptMs = GetDouble(timingJson, "prompt_ms");
            if (promptMs.HasValue && response.PromptProcessingMs == 0)
                response.PromptProcessingMs = promptMs.Value;

            var genMs2 = GetDouble(timingJson, "predicted_ms") ?? GetDouble(timingJson, "eval_ms");
            if (genMs2.HasValue && response.GenerationMs == 0)
                response.GenerationMs = genMs2.Value;
        }
    }

    /// <summary>
    /// Parses a llama-cpp-server timings JSON element into LlamaCppTimings.
    /// Handles both "timings" format (with generation_ms) and "timing" format (with predicted_ms).
    /// </summary>
    private static LlamaCppTimings ParseLlamaCppTimings(JsonElement element)
    {
        var result = new LlamaCppTimings();

        // Prompt metrics
        if (element.TryGetProperty("prompt_n", out JsonElement pn)) result.PromptN = pn.GetInt32();
        if (element.TryGetProperty("prompt_ms", out JsonElement pm)) result.PromptMs = pm.GetDouble();
        if (element.TryGetProperty("prompt_per_token_ms", out JsonElement ptm)) result.PromptPerTokenMs = ptm.GetDouble();
        if (element.TryGetProperty("prompt_per_second", out JsonElement pps)) result.PromptPerSecond = pps.GetDouble();

        // Cache metrics
        if (element.TryGetProperty("cache_n", out JsonElement cn) && cn.ValueKind == JsonValueKind.Number) result.CacheN = cn.GetInt32();

        // Generation metrics (may be named generation_* or predicted_* depending on version)
        if (element.TryGetProperty("generation_n", out JsonElement gn)) result.GenerationN = gn.GetInt32();
        if (element.TryGetProperty("generation_ms", out JsonElement gm)) result.GenerationMs = gm.GetDouble();
        if (element.TryGetProperty("generation_per_token_ms", out JsonElement gptm)) result.GenerationPerTokenMs = gptm.GetDouble();
        if (element.TryGetProperty("generation_per_second", out JsonElement gps)) result.GenerationPerSecond = gps.GetDouble();

        // Predicted/speculative decoding metrics
        if (element.TryGetProperty("predicted_n", out JsonElement predn) && predn.ValueKind == JsonValueKind.Number) result.PredictedN = predn.GetInt32();
        if (element.TryGetProperty("predicted_ms", out JsonElement predm)) result.PredictedMs = predm.GetDouble();
        if (element.TryGetProperty("predicted_per_token_ms", out JsonElement preptm)) result.PredictedPerTokenMs = preptm.GetDouble();
        if (element.TryGetProperty("predicted_per_second", out JsonElement prepps)) result.PredictedPerSecond = prepps.GetDouble();

        return result;
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
