using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Scoped, per-request holder for the API key resolved by <c>ApiKeyAuthFilter</c> (if any).
/// Populated once per request before a protocol handler runs, then read by that handler to
/// scope which models/presets are visible/usable for the current caller.
/// </summary>
public interface IApiKeyRequestContext
{
    /// <summary>
    /// The API key that authenticated the current request. Null when API-key auth is disabled
    /// (<c>GatewaySettings.RequireApiKey == false</c>) or the request didn't go through the filter
    /// (e.g. it's not a protocol endpoint) — in both cases every model is considered allowed.
    /// </summary>
    ApiKey? CurrentKey { get; set; }

    /// <summary>
    /// Whether the given preset is usable by the current caller.
    /// </summary>
    bool IsModelAllowed(Guid presetId);

    /// <summary>
    /// Filters a preset collection down to the ones the current caller may see/use.
    /// </summary>
    IEnumerable<ModelPreset> FilterAllowed(IEnumerable<ModelPreset> presets);
}
