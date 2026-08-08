using System.Text.Json.Serialization;

namespace LR.Core.Models.Ollama;

/// <summary>
/// Request body for Ollama chat API.
/// </summary>
public class ChatRequest
{
    /// <summary>
    /// The model to use. Should match a configured preset name.
    /// </summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// The conversation messages.
    /// </summary>
    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = new();

    /// <summary>
    /// Whether to stream the response as JSON lines. Defaults to true, matching Ollama's API behavior
    /// (a client that omits "stream" expects a streamed response).
    /// </summary>
    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = true;

    /// <summary>
    /// Additional options for model inference.
    /// </summary>
    [JsonPropertyName("options")]
    public ChatOptions? Options { get; set; }
}

public class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

public class ChatOptions
{
    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("num_predict")]
    public int? NumPredict { get; set; }

    [JsonPropertyName("top_k")]
    public int? TopK { get; set; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    [JsonPropertyName("min_p")]
    public double? MinP { get; set; }

    [JsonPropertyName("repeat_penalty")]
    public double? RepeatPenalty { get; set; }

    [JsonPropertyName("presence_penalty")]
    public double? PresencePenalty { get; set; }

    [JsonPropertyName("frequency_penalty")]
    public double? FrequencyPenalty { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    [JsonPropertyName("stop")]
    public string[]? Stop { get; set; }
}
