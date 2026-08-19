using System.Text.Json;
using System.Text.Json.Serialization;

namespace LR.Core.Models.Claude;

/// <summary>
/// Request body for Claude Messages API.
/// </summary>
public class CreateMessageRequest
{
    /// <summary>
    /// The model to use. Should match a configured preset name.
    /// </summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// System prompt - instructions for the assistant. Can be a plain string or an array of
    /// content blocks (e.g. cached system prompt blocks with cache_control), same as message content.
    /// </summary>
    [JsonPropertyName("system")]
    [JsonConverter(typeof(MessageContentConverter))]
    public MessageContent? System { get; set; }

    /// <summary>
    /// Tool definitions available to the model. Preserved as raw JSON and forwarded as-is —
    /// this router doesn't need to interpret tool schemas, only avoid dropping them.
    /// </summary>
    [JsonPropertyName("tools")]
    public JsonElement? Tools { get; set; }

    /// <summary>
    /// Controls whether/which tool the model must use. Preserved as raw JSON.
    /// </summary>
    [JsonPropertyName("tool_choice")]
    public JsonElement? ToolChoice { get; set; }

    /// <summary>
    /// Opaque request metadata (e.g. user_id). Preserved as raw JSON.
    /// </summary>
    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; set; }

    /// <summary>
    /// The conversation messages.
    /// </summary>
    [JsonPropertyName("messages")]
    public List<MessageParam> Messages { get; set; } = new();

    /// <summary>
    /// Maximum number of tokens to generate.
    /// </summary>
    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; }

    /// <summary>
    /// Whether to stream the response as Server-Sent Events.
    /// </summary>
    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    /// <summary>
    /// Amount of randomness injected into the response. Defaults to 1.0.
    /// </summary>
    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    /// <summary>
    /// Use nucleus sampling.
    /// </summary>
    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    /// <summary>
    /// Custom text sequences that will cause the model to stop generating.
    /// </summary>
    [JsonPropertyName("stop_sequences")]
    public string[]? StopSequences { get; set; }
}
