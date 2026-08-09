using System.Text.Json;
using System.Text.Json.Serialization;

namespace LR.Core.Models.OpenAI.Responses;

/// <summary>
/// A tool definition in the Responses API's flat shape (name/parameters at the top level,
/// unlike Chat Completions which nests them under "function"). Only "function" tools are
/// supported — llama.cpp cannot execute OpenAI's built-in tools (web_search, file_search,
/// code_interpreter, computer_use, image_generation, mcp).
/// </summary>
public class ResponseTool
{
    [JsonPropertyName("type")]
    [JsonConverter(typeof(StringOrFunctionConverter))]
    public string Type { get; set; } = "function";

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; set; }

    [JsonPropertyName("strict")]
    public bool? Strict { get; set; }

    public ChatTool ToChatTool() => new()
    {
        Type = "function",
        Function = new ChatToolFunction { Name = Name, Description = Description, Parameters = Parameters }
    };

    public static ResponseTool FromChatTool(ChatTool tool) => new()
    {
        Type = "function",
        Name = tool.Function?.Name ?? string.Empty,
        Description = tool.Function?.Description,
        Parameters = tool.Function?.Parameters
    };
}
