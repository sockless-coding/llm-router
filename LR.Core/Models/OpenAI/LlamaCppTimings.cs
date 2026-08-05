using System.Text.Json.Serialization;

namespace LR.Core.Models.OpenAI;

/// <summary>
/// Timing information included in the final SSE chunk, matching llama-cpp-server format.
/// </summary>
public class LlamaCppTimings
{
    /// <summary>Number of tokens loaded from KV cache.</summary>
    [JsonPropertyName("cache_n")]
    public int? CacheN { get; set; }

    /// <summary>Number of prompt tokens processed (not from cache).</summary>
    [JsonPropertyName("prompt_n")]
    public int PromptN { get; set; }

    /// <summary>Prompt processing time in milliseconds.</summary>
    [JsonPropertyName("prompt_ms")]
    public double? PromptMs { get; set; }

    /// <summary>Average prompt token processing time in milliseconds per token.</summary>
    [JsonPropertyName("prompt_per_token_ms")]
    public double? PromptPerTokenMs { get; set; }

    /// <summary>Prompt processing throughput (tokens/sec).</summary>
    [JsonPropertyName("prompt_per_second")]
    public double? PromptPerSecond { get; set; }

    // --- Generation metrics (mapped from generation phase) ---

    /// <summary>Number of generated tokens.</summary>
    [JsonPropertyName("generation_n")]
    public int? GenerationN { get; set; }

    /// <summary>Total generation time in milliseconds.</summary>
    [JsonPropertyName("generation_ms")]
    public double? GenerationMs { get; set; }

    /// <summary>Average generation time per token in milliseconds.</summary>
    [JsonPropertyName("generation_per_token_ms")]
    public double? GenerationPerTokenMs { get; set; }

    /// <summary>Generation throughput (tokens/sec).</summary>
    [JsonPropertyName("generation_per_second")]
    public double? GenerationPerSecond { get; set; }

    // --- Speculative decoding metrics ---

    /// <summary>Number of draft tokens accepted during speculative decoding.</summary>
    [JsonPropertyName("predicted_n")]
    public int? PredictedN { get; set; }

    /// <summary>Total time spent on predicted/draft token evaluation in milliseconds.</summary>
    [JsonPropertyName("predicted_ms")]
    public double? PredictedMs { get; set; }

    /// <summary>Average predicted token evaluation time in milliseconds per token.</summary>
    [JsonPropertyName("predicted_per_token_ms")]
    public double? PredictedPerTokenMs { get; set; }

    /// <summary>Predicted/draft token throughput (tokens/sec).</summary>
    [JsonPropertyName("predicted_per_second")]
    public double? PredictedPerSecond { get; set; }
}
