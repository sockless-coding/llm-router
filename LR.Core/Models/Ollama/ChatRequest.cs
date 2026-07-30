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
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// The conversation messages.
    /// </summary>
    public List<ChatMessage> Messages { get; set; } = new();

    /// <summary>
    /// Whether to stream the response as JSON lines.
    /// </summary>
    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

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

    [JsonPropertyName("stop")]
    public string[]? Stop { get; set; }
}
