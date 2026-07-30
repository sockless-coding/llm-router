using System.Text.Json.Serialization;

namespace LR.Core.Models.Ollama;

/// <summary>
/// Request body for Ollama /api/embed endpoint.
/// </summary>
public class EmbedRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Text or list of text to generate embeddings for.
    /// </summary>
    [JsonPropertyName("input")]
    public object Input { get; set; } = new List<string>();

    /// <summary>
    /// Truncates input to fit within context length. Defaults to true.
    /// </summary>
    [JsonPropertyName("truncate")]
    public bool? Truncate { get; set; }

    /// <summary>
    /// Additional model parameters.
    /// </summary>
    [JsonPropertyName("options")]
    public ChatOptions? Options { get; set; }

    /// <summary>
    /// How long the model stays loaded in memory (e.g., "5m", "0").
    /// </summary>
    [JsonPropertyName("keep_alive")]
    public string? KeepAlive { get; set; }

    /// <summary>
    /// Number of dimensions for the embedding.
    /// </summary>
    [JsonPropertyName("dimensions")]
    public int? Dimensions { get; set; }
}

