using System.ComponentModel.DataAnnotations;

namespace LR.Core.Models;

/// <summary>
/// An incoming LLM inference request that the routing engine evaluates.
/// </summary>
public class RouteRequest
{
    /// <summary>
    /// Optional model name hint for routing.
    /// </summary>
    public string? ModelName { get; set; }

    /// <summary>
    /// Optional preset ID hint for routing.
    /// </summary>
    public Guid? PresetId { get; set; }

    /// <summary>
    /// Optional backend type hint for routing.
    /// </summary>
    public BackendType? PreferredBackend { get; set; }

    /// <summary>
    /// The wire protocol <see cref="Payload"/> is encoded in. Backend providers that don't
    /// natively speak every protocol use this to decide how to translate/route the payload
    /// (e.g. llama.cpp accepts this shape directly on a protocol-specific endpoint rather than
    /// needing conversion to OpenAI's chat-completions shape).
    /// </summary>
    public ApiProtocol Protocol { get; set; } = ApiProtocol.OpenAI;

    /// <summary>
    /// The inference payload (e.g., prompt text, JSON body).
    /// </summary>
    public string Payload { get; set; } = string.Empty;
}
