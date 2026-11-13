using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Whether a preset's model is in memory right now, as reported to API clients on
/// <c>/v1/models/capabilities</c> so they can tell a model that answers immediately from one
/// that will cold-load (or evict another model) on first request.
/// </summary>
public static class PresetLoadState
{
    /// <summary>Running, healthy, and serving this preset: requests are answered without a load.</summary>
    public const string Loaded = "loaded";

    /// <summary>The server is starting (or re-attaching, or not yet healthy) with this preset.</summary>
    public const string Loading = "loading";

    /// <summary>Not in memory: the server is stopped, or loaded with a different preset.</summary>
    public const string Unloaded = "unloaded";

    /// <summary>The server's last attempt to run this preset failed.</summary>
    public const string Error = "error";

    /// <summary>
    /// The load state of <paramref name="presetId"/> on <paramref name="instance"/>, the server the
    /// preset belongs to (null when that server no longer exists).
    /// </summary>
    public static string Of(ServerInstance? instance, Guid presetId)
    {
        if (instance is null || instance.ActivePresetId != presetId)
            return Unloaded;

        return instance.Status switch
        {
            ServerStatus.Running => instance.IsHealthy ? Loaded : Loading,
            ServerStatus.Starting or ServerStatus.Reconnecting => Loading,
            ServerStatus.Error => Error,
            _ => Unloaded,
        };
    }
}
