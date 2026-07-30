using System.Text.Json.Serialization;

namespace LR.Core.Models.Ollama;

/// <summary>
/// Request body for Ollama /api/generate endpoint.
/// </summary>
public class GenerateRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// The prompt to generate a response for.
    /// </summary>
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    /// <summary>
    /// Text after the model response (for infilling models).
    /// </summary>
    [JsonPropertyName("suffix")]
    public string? Suffix { get; set; }

    /// <summary>
    /// Base64-encoded images for multimodal models.
    /// </summary>
    [JsonPropertyName("images")]
    public List<string>? Images { get; set; }

    /// <summary>
    /// Format to return response in ("json" or a JSON schema).
    /// </summary>
    [JsonPropertyName("format")]
    public object? Format { get; set; }

    /// <summary>
    /// Additional model parameters.
    /// </summary>
    [JsonPropertyName("options")]
    public ChatOptions? Options { get; set; }

    /// <summary>
    /// System message to override what is defined in the Modelfile.
    /// </summary>
    [JsonPropertyName("system")]
    public string? System { get; set; }

    /// <summary>
    /// Prompt template to use (overrides what is defined in the Modelfile).
    /// </summary>
    [JsonPropertyName("template")]
    public string? Template { get; set; }

    /// <summary>
    /// If false, returns a single response object instead of streaming.
    /// </summary>
    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = true;

    /// <summary>
    /// If true, no formatting will be applied to the prompt.
    /// </summary>
    [JsonPropertyName("raw")]
    public bool Raw { get; set; }

    /// <summary>
    /// How long the model stays loaded in memory (e.g., "5m", "0", "-1").
    /// </summary>
    [JsonPropertyName("keep_alive")]
    public string? KeepAlive { get; set; }
}

