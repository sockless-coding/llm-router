using System.Text.Json.Serialization;

namespace LR.Core.Models.Ollama;

/// <summary>
/// Request body for the Ollama /api/show endpoint.
/// </summary>
public class ShowRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }
}

/// <summary>
/// Response for the Ollama /api/show endpoint.
/// Matches the Ollama API spec at https://docs.ollama.com/api-reference/show-model-details
/// </summary>
public class ShowResponse
{
    /// <summary>
    /// Model parameter settings serialized as text (e.g. "temperature 0.8\nnum_ctx 2048").
    /// </summary>
    [JsonPropertyName("parameters")]
    public string? Parameters { get; set; }

    /// <summary>
    /// The license of the model.
    /// </summary>
    [JsonPropertyName("license")]
    public string? License { get; set; }

    /// <summary>
    /// Last modified timestamp in ISO 8601 format.
    /// </summary>
    [JsonPropertyName("modified_at")]
    public string? ModifiedAt { get; set; }

    /// <summary>
    /// High-level model details (architecture, parameter size, quantization, etc.).
    /// </summary>
    [JsonPropertyName("details")]
    public ShowDetails? Details { get; set; }

    /// <summary>
    /// The template used by the model to render prompts.
    /// </summary>
    [JsonPropertyName("template")]
    public string? Template { get; set; }

    /// <summary>
    /// List of supported features (e.g. "completion", "vision").
    /// </summary>
    [JsonPropertyName("capabilities")]
    public string[]? Capabilities { get; set; }

    /// <summary>
    /// Additional model metadata from GGUF header key-value pairs.
    /// Excludes large binary arrays (tokenizer tokens, merges, etc.).
    /// </summary>
    [JsonPropertyName("model_info")]
    public Dictionary<string, object>? ModelInfo { get; set; }
}

public class ShowDetails
{
    [JsonPropertyName("parent_model")]
    public string? ParentModel { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonPropertyName("family")]
    public string? Family { get; set; }

    [JsonPropertyName("families")]
    public string[]? Families { get; set; }

    [JsonPropertyName("parameter_size")]
    public string? ParameterSize { get; set; }

    [JsonPropertyName("quantization_level")]
    public string? QuantizationLevel { get; set; }
}
