using System.Text.Json.Serialization;

namespace LR.Core.Models.Ollama;

/// <summary>
/// Model info for the /api/ps endpoint.
/// </summary>
public class PsModelInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("digest")]
    public string Digest { get; set; } = string.Empty;

    [JsonPropertyName("details")]
    public PsModelDetails? Details { get; set; }

    [JsonPropertyName("expires_at")]
    public string? ExpiresAt { get; set; }

    [JsonPropertyName("size_vram")]
    public long SizeVram { get; set; }
}

/// <summary>
/// Model details for the /api/ps endpoint.
/// </summary>
public class PsModelDetails
{
    [JsonPropertyName("parent_model")]
    public string? ParentModel { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonPropertyName("family")]
    public string? Family { get; set; }

    [JsonPropertyName("families")]
    public List<string>? Families { get; set; }

    [JsonPropertyName("parameter_size")]
    public string? ParameterSize { get; set; }

    [JsonPropertyName("quantization_level")]
    public string? QuantizationLevel { get; set; }
}

