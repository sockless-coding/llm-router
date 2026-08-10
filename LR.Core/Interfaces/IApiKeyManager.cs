using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Manages API keys used to authenticate inbound requests to the protocol endpoints, and their
/// per-key model scoping.
/// </summary>
public interface IApiKeyManager
{
    /// <summary>
    /// Creates a new key. Returns the persisted entity together with the one-time raw key value
    /// — only a hash of the raw key is stored, so this is the only time it's ever available.
    /// </summary>
    Task<(ApiKey Key, string RawKey)> CreateAsync(string name, bool allowAllModels, IEnumerable<Guid> allowedPresetIds);

    /// <summary>
    /// Issues a new raw key value for an existing key, invalidating the old one. Name and
    /// model-scoping configuration are unchanged. Returns null if the key doesn't exist.
    /// </summary>
    Task<(ApiKey Key, string RawKey)?> RegenerateAsync(Guid id);

    /// <summary>
    /// Updates a key's name, enabled state, and model scoping in place.
    /// </summary>
    Task<bool> UpdateAsync(Guid id, string name, bool isEnabled, bool allowAllModels, IEnumerable<Guid> allowedPresetIds);

    Task<bool> DeleteAsync(Guid id);

    Task<IReadOnlyList<ApiKey>> GetAllAsync();

    Task<ApiKey?> GetByIdAsync(Guid id);

    /// <summary>
    /// Validates a raw key presented by a client: hashes it, looks it up, and — if found and
    /// enabled — stamps <see cref="ApiKey.LastUsedAt"/> and returns it (with allowed-preset ids
    /// loaded). Returns null for an unknown, disabled, or malformed key.
    /// </summary>
    Task<ApiKey?> ValidateAsync(string rawKey);
}
