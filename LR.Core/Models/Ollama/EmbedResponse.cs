using System.Text.Json.Serialization;

namespace LR.Core.Models.Ollama;

/// <summary>
/// Response for Ollama /api/embed endpoint.
/// </summary>
public class EmbedResponse
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Array of embeddings (one per input).
    /// Each embedding is an array of floats.
    /// </summary>
    [JsonPropertyName("embeddings")]
    public List<List<double>> Embeddings { get; set; } = new();

    [JsonPropertyName("total_duration")]
    public long TotalDuration { get; set; }

    [JsonPropertyName("load_duration")]
    public long LoadDuration { get; set; }

    [JsonPropertyName("prompt_eval_count")]
    public int PromptEvalCount { get; set; }
}

