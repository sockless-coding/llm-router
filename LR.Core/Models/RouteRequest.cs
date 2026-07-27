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
    /// The inference payload (e.g., prompt text, JSON body).
    /// </summary>
    public string Payload { get; set; } = string.Empty;
}
