using System.Text.Json.Serialization;

namespace LR.Core.Models.OpenAI;

/// <summary>
/// Non-streaming response for OpenAI-compatible chat completions.
/// </summary>
public class ChatCompletionResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("object")]
    public string Object { get; set; } = "chat.completion";

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("choices")]
    public List<Choice> Choices { get; set; } = new();

    [JsonPropertyName("usage")]
    public Usage? Usage { get; set; }

    /// <summary>
    /// A unique fingerprint of the system that generated the response.
    /// </summary>
    [JsonPropertyName("system_fingerprint")]
    public string? SystemFingerprint { get; set; }

    /// <summary>
    /// The service tier used for this request (e.g., "auto" or "default").
    /// </summary>
    [JsonPropertyName("service_tier")]
    public string? ServiceTier { get; set; }
}

public class Choice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public ChatMessage Message { get; set; } = new();

    /// <summary>
    /// The reason the model stopped generating tokens.
    /// </summary>
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }

    /// <summary>
    /// Log probability information for the choice.
    /// </summary>
    [JsonPropertyName("logprobs")]
    public ChoiceLogprobs? Logprobs { get; set; }
}

public class Usage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }

    /// <summary>
    /// Breakdown of input token usage (cached tokens, audio tokens).
    /// </summary>
    [JsonPropertyName("prompt_tokens_details")]
    public InputTokenDetails? PromptTokensDetails { get; set; }

    /// <summary>
    /// Breakdown of output token usage (reasoning tokens, audio tokens).
    /// </summary>
    [JsonPropertyName("completion_tokens_details")]
    public OutputTokenDetails? CompletionTokensDetails { get; set; }
}
