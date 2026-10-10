using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LR.Core.Models;

/// <summary>
/// A device-memory budget shared by every server whose <see cref="ServerInstance.MemoryGroup"/>
/// has this name — typically one GPU. See <see cref="LR.Core.Services.MemoryGroupScheduler"/>.
/// </summary>
[Table("MemoryGroups")]
public class MemoryGroup
{
    [Key, MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Usable device memory for the group, in MB. 0 = no budget enforced.</summary>
    public int BudgetMb { get; set; }
}
