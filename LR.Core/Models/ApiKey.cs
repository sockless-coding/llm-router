using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LR.Core.Models;

/// <summary>
/// An API key that clients present to authenticate against the router's protocol endpoints.
/// Only a salted hash of the raw key is ever persisted — the raw value is shown to the admin
/// once, at creation/regeneration time.
/// </summary>
[Table("ApiKeys")]
public class ApiKey
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 hash (hex) of the raw key. Used to look up and validate incoming keys.
    /// </summary>
    [Required, MaxLength(64)]
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>
    /// Leading portion of the raw key (e.g. "lr-a1b2c3d4"), kept for display/identification
    /// in the dashboard since the full key can't be recovered from the hash.
    /// </summary>
    [Required, MaxLength(24)]
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Whether this key currently authenticates. Lets an admin revoke access without deleting
    /// the key (and its model-scoping configuration).
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// When true, this key can access every model/preset. When false, access is limited to the
    /// presets listed in <see cref="AllowedPresets"/>.
    /// </summary>
    public bool AllowAllModels { get; set; } = true;

    [Required]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>
    /// Navigation: explicit model scoping rows. Only consulted when <see cref="AllowAllModels"/> is false.
    /// </summary>
    public List<ApiKeyModelPreset> AllowedPresets { get; set; } = new();
}
