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
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// System prompt - instructions for the assistant.
    /// </summary>
    [JsonPropertyName("system")]
    public string? System { get; set; }

    /// <summary>
    /// The conversation messages.
    /// </summary>
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
