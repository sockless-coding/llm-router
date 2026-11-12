using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LR.Core.Models;

/// <summary>What a <see cref="DiscoveryExclusion"/> keeps out of the registry.</summary>
public enum DiscoveryExclusionKind
{
    /// <summary>A model file or checkpoint folder under the model library root.</summary>
    Model = 0,

    /// <summary>An install folder under the engine install root.</summary>
    Engine = 1,
}

/// <summary>
/// A path on this machine that was removed from the model library or the engine registry while its
/// files were kept. Auto-discovery (<see cref="Services.SharedLibraryDiscovery"/>) skips it, so a
/// removed entry doesn't come back on the next scan. Importing the path again by hand clears it.
/// </summary>
[Table("DiscoveryExclusions")]
public class DiscoveryExclusion
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public DiscoveryExclusionKind Kind { get; set; }

    /// <summary>Full path, without a trailing separator.</summary>
    [Required, MaxLength(1024)]
    public string Path { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
