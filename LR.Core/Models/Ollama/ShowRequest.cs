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
/// </summary>
public class ShowResponse
{
    /// <summary>
    /// The Modelfile content (best-effort reconstruction from preset).
    /// </summary>
    [JsonPropertyName("modelfile")]
    public string? Modelfile { get; set; }

    /// <summary>
    /// Model parameters as key-value pairs.
    /// </summary>
    [JsonPropertyName("parameters")]
    public string? Parameters { get; set; }

    /// <summary>
    /// Projectors (empty for llama.cpp models).
    /// </summary>
    [JsonPropertyName("projectors")]
    public string? Projectors { get; set; }

    /// <summary>
    /// General model information.
    /// </summary>
    [JsonPropertyName("details")]
    public ShowDetails? Details { get; set; }

    /// <summary>
    /// Examine info (empty for llama.cpp models).
    /// </summary>
    [JsonPropertyName("examine")] 
    public object? Examine { get; set; }

    /// <summary>
    /// Template used by the model.
    /// </summary>
    [JsonPropertyName("template")]
    public string? Template { get; set; }
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
