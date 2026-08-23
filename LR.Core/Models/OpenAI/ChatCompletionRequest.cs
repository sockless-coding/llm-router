using System.Text.Json;
using System.Text.Json.Serialization;

namespace LR.Core.Models.OpenAI;

/// <summary>
/// Request body for OpenAI-compatible chat completions API.
/// </summary>
public class ChatCompletionRequest
{
    /// <summary>
    /// The model to use. Should match a configured preset name.
    /// </summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// The messages to send for the conversation.
    /// </summary>
    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = new();

    /// <summary>
    /// What sampling temperature to use. Between 0 and 2.
    /// </summary>
    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    /// <summary>
    /// An alternative to sampling with temperature, called nucleus sampling.
    /// </summary>
    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    /// <summary>
    /// How many chat completion choices to generate.
    /// </summary>
    [JsonPropertyName("n")]
    public int N { get; set; } = 1;

    /// <summary>
    /// Whether to stream the response as Server-Sent Events.
    /// </summary>
    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    /// <summary>
    /// Sequences where the API will stop generating further tokens.
    /// </summary>
    [JsonPropertyName("stop")]
    public string[]? Stop { get; set; }

    /// <summary>
    /// The maximum number of tokens to generate (deprecated, use MaxCompletionTokens).
    /// </summary>
    [Obsolete("Use MaxCompletionTokens instead.")]
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    /// <summary>
    /// The maximum number of completion tokens to generate.
    /// </summary>
    [JsonPropertyName("max_completion_tokens")]
    public int? MaxCompletionTokens { get; set; }

    /// <summary>
    /// Number between -2.0 and 2.0. Positive values penalize new tokens based on their existing frequency.
    /// </summary>
    [JsonPropertyName("frequency_penalty")]
    public float? FrequencyPenalty { get; set; }

    /// <summary>
    /// Number between -2.0 and 2.0. Positive values penalize new tokens based on whether they appear in the text so far.
    /// </summary>
    [JsonPropertyName("presence_penalty")]
    public float? PresencePenalty { get; set; }

    // --- New fields from OpenAI spec ---

    /// <summary>
    /// If specified, our system will make a best effort to sample deterministically,
    /// such that repeated requests with the same seed and parameters should return the same result.
    /// </summary>
    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    /// <summary>
    /// A suffix to append after the generated tokens (deprecated for chat completions).
    /// </summary>
    [Obsolete("Suffix is deprecated for chat completions.")]
    [JsonPropertyName("suffix")]
    public string? Suffix { get; set; }

    /// <summary>
    /// An object specifying the format that the model must output.
    /// Setting to a JSON Schema with strict=true enables Structured Outputs.
    /// </summary>
    [JsonPropertyName("response_format")]
    public ChatResponseFormat? ResponseFormat { get; set; }

    /// <summary>
    /// A list of tools the model may call. Currently only functions are supported.
    /// </summary>
    [JsonPropertyName("tools")]
    public List<ChatTool>? Tools { get; set; }

    /// <summary>
    /// Controls which (if any) tool is called by the model.
    /// Can be "none", "auto", "required", or an object specifying a specific function.
    /// </summary>
    [JsonPropertyName("tool_choice")]
    public ChatToolChoice? ToolChoice { get; set; }

    /// <summary>
    /// Whether to allow parallel tool calls. Defaults to true when tools are provided.
    /// </summary>
    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; set; }

    /// <summary>
    /// Modify the likelihood of specified tokens appearing in the completion.
    /// Keys are token IDs and values are biases between -100 and 100.
    /// </summary>
    [JsonPropertyName("logit_bias")]
    public Dictionary<int, int>? LogitBias { get; set; }

    /// <summary>
    /// A unique identifier representing your end-user.
    /// This can help OpenAI to monitor and detect abuse.
    /// </summary>
    [JsonPropertyName("user")]
    public string? User { get; set; }

    /// <summary>
    /// Options for streaming response. If set, an additional chunk will be streamed before "[DONE]"
    /// with usage information in the last chunk.
    /// </summary>
    [JsonPropertyName("stream_options")]
    public StreamOptions? StreamOptions { get; set; }

    /// <summary>
    /// Whether to store the completion for later retrieval via API. Defaults to false.
    /// </summary>
    [JsonPropertyName("store")]
    public bool? Store { get; set; }

    /// <summary>
    /// A list of metadata key-value pairs to include with the request.
    /// </summary>
    [JsonPropertyName("metadata")]
    public List<ChatMetadata>? Metadata { get; set; }

    /// <summary>
    /// Specifies the latency tier for the request. "auto" or "default".
    /// </summary>
    [JsonPropertyName("service_tier")]
    public string? ServiceTier { get; set; }

    /// <summary>
    /// Whether to return log probabilities of the output tokens.
    /// </summary>
    [JsonPropertyName("logprobs")]
    public bool? Logprobs { get; set; }

    /// <summary>
    /// The maximum number of top log probability tokens to return per output position.
    /// Must be between 0 and 5 when logprobs is true.
    /// </summary>
    [JsonPropertyName("top_logprobs")]
    public int? TopLogprobs { get; set; }

    /// <summary>
    /// A prediction to evaluate. Can contain a single message with type "content".
    /// </summary>
    [JsonPropertyName("prediction")]
    public ChatPrediction? Prediction { get; set; }

    // --- llama.cpp server extensions (not part of the official OpenAI spec, but accepted
    // by llama.cpp's /v1/chat/completions as top-level sampling params) ---

    /// <summary>Limits next-token selection to the K most probable tokens.</summary>
    [JsonPropertyName("top_k")]
    public int? TopK { get; set; }

    /// <summary>Minimum probability (relative to the most likely token) for a token to be considered.</summary>
    [JsonPropertyName("min_p")]
    public float? MinP { get; set; }

    /// <summary>Penalizes tokens that have already appeared, to reduce repetition.</summary>
    [JsonPropertyName("repeat_penalty")]
    public float? RepeatPenalty { get; set; }

    /// <summary>
    /// Per-request override for reasoning depth (e.g. "low", "medium", "high"), forwarded
    /// straight through to the backend as a chat-template kwarg. Only takes effect on presets
    /// whose chat template reads a reasoning-effort-style variable — see
    /// <c>ModelCapabilitiesInfo.SupportsReasoningEffort</c>. Overrides the preset's own
    /// <c>--reasoning-effort</c> launch default for this request only.
    /// </summary>
    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }
}

/// <summary>
/// Options for streaming responses.
/// </summary>
public class StreamOptions
{
    /// <summary>
    /// If set, an additional chunk will be streamed before "[DONE]"
    /// containing the final usage statistics.
    /// </summary>
    [JsonPropertyName("include_usage")]
    public bool IncludeUsage { get; set; }
}

/// <summary>
/// A metadata key-value pair.
/// </summary>
public class ChatMetadata
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// A prediction to evaluate.
/// </summary>
public class ChatPrediction
{
    /// <summary>The type of the prediction. Currently only "content" is supported.</summary>
    [JsonPropertyName("type")]
    [JsonConverter(typeof(StringOrFunctionConverter))]
    public string Type { get; set; } = "content";

    /// <summary>The content to predict.</summary>
    [JsonPropertyName("content")]
    public object? Content { get; set; }
}
