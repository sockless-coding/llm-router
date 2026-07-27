using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LR.Core.Models;

/// <summary>
/// A rule used by the routing engine to match incoming requests.
/// </summary>
[Table("RoutingRules")]
public class RoutingRule
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Lower priority = evaluated first.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Optional model name to match (case-insensitive).
    /// </summary>
    [MaxLength(256)]
    public string? ModelName { get; set; }

    /// <summary>
    /// Optional preset ID to match.
    /// </summary>
    public Guid? PresetId { get; set; }

    /// <summary>
    /// Optional backend type to match.
    /// </summary>
    public BackendType? BackendType { get; set; }

    /// <summary>
    /// The target server instance this rule routes to.
    /// </summary>
    [Required, ForeignKey(nameof(TargetServerInstance))]
    public Guid TargetServerInstanceId { get; set; }

    /// <summary>
    /// Navigation: the target server instance.
    /// </summary>
    public ServerInstance? TargetServerInstance { get; set; }
}
