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

    /// <summary>
    /// The API key that authenticated the client for this request, if any. Carried through the
    /// routing/queue pipeline so statistics can be attributed per key even when the request is
    /// recorded from a background dispatch scope that never saw the HTTP auth context. Null when
    /// API-key auth is disabled or the request arrived unauthenticated.
    /// </summary>
    public Guid? ApiKeyId { get; set; }
}
