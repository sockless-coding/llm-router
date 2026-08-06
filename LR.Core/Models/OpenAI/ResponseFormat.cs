using System.Text.Json;
using System.Text.Json.Serialization;

namespace LR.Core.Models.OpenAI;

/// <summary>
/// An object specifying the format that the model must output.
/// Setting to a JSON Schema with strict=true enables Structured Outputs.
/// </summary>
public class ChatResponseFormat
{
    /// <summary>Must be one of "text", "json_object", or "json_schema".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    /// <summary>The JSON Schema definition when type is "json_schema".</summary>
    [JsonPropertyName("json_schema")]
    public ChatResponseFormatJsonSchema? JsonSchema { get; set; }
}

/// <summary>
/// The JSON schema for structured output.
/// </summary>
public class ChatResponseFormatJsonSchema
{
    /// <summary>The name of the response format.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>An optional description for the response format.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>The JSON Schema object defining the structure of the output.</summary>
    [JsonPropertyName("schema")]
    public JsonElement? Schema { get; set; }

    /// <summary>Whether to enforce strict schema adherence. Defaults to false.</summary>
    [JsonPropertyName("strict")]
    public bool? Strict { get; set; }
}
