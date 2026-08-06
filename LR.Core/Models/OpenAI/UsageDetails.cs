using System.Text.Json.Serialization;

namespace LR.Core.Models.OpenAI;

/// <summary>
/// Breakdown of input token usage.
/// </summary>
public class InputTokenDetails
{
    /// <summary>Number of tokens cached from previous requests.</summary>
    [JsonPropertyName("cached_tokens")]
    public int CachedTokens { get; set; }

    /// <summary>Number of audio input tokens.</summary>
    [JsonPropertyName("audio_tokens")]
    public int AudioTokens { get; set; }
}

/// <summary>
/// Breakdown of output token usage.
/// </summary>
public class OutputTokenDetails
{
    /// <summary>Number of tokens used for reasoning (e.g., Chain-of-Thought).</summary>
    [JsonPropertyName("reasoning_tokens")]
    public int ReasoningTokens { get; set; }

    /// <summary>Number of audio output tokens.</summary>
    [JsonPropertyName("audio_tokens")]
    public int AudioTokens { get; set; }
}
