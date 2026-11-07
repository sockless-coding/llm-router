using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LR.Core.Models;

/// <summary>
/// A remote machine running <c>LR.Agent</c>: it connects out to the router, supervises the
/// backends of the servers assigned to it (<see cref="ServerInstance.AgentId"/>), and installs
/// engines (<see cref="EngineBuild.AgentId"/>) and models (<see cref="LocalModel.AgentId"/>) on
/// its own disk when told to. Created when an agent first enrolls with the router's enrollment
/// token; afterwards it authenticates with its own secret (stored here only as a hash).
/// </summary>
[Table("RemoteAgents")]
public class RemoteAgent
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name — the machine's host name on enrollment, renamable.</summary>
    [Required, MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 (hex) of the agent's secret. The secret itself only ever lives on the agent.</summary>
    [Required, MaxLength(128)]
    public string SecretHash { get; set; } = string.Empty;

    /// <summary>Stable per-install id the agent generates, so a reinstall over an existing install keeps its identity.</summary>
    [MaxLength(128)]
    public string? MachineId { get; set; }

    /// <summary>A disabled agent is refused when it connects; its servers can't start.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Push new agent versions to this agent as soon as it connects with an older one.</summary>
    public bool AutoUpdate { get; set; } = true;

    // ==================== REPORTED BY THE AGENT ====================

    [MaxLength(256)]
    public string? Hostname { get; set; }

    /// <summary><c>windows</c> or <c>linux</c>.</summary>
    [MaxLength(32)]
    public string? Os { get; set; }

    /// <summary><c>x64</c> or <c>arm64</c>.</summary>
    [MaxLength(32)]
    public string? Arch { get; set; }

    /// <summary>The .NET runtime identifier of the agent's package (<c>linux-arm64</c>, …), used to pick its update.</summary>
    [MaxLength(32)]
    public string? Rid { get; set; }

    [MaxLength(64)]
    public string? Version { get; set; }

    /// <summary>Human-readable OS description (e.g. <c>Ubuntu 24.04.1 LTS</c>).</summary>
    [MaxLength(256)]
    public string? OsDescription { get; set; }

    /// <summary>Folder the agent keeps its engines/models/state under, unless told otherwise.</summary>
    [MaxLength(1024)]
    public string? DataDirectory { get; set; }

    /// <summary>The address the router saw the agent's last connection come from.</summary>
    [MaxLength(256)]
    public string? LastRemoteAddress { get; set; }

    /// <summary>Hardware/usage snapshot (CPU, memory, disk, GPUs) as JSON — see <see cref="Agents.AgentSystemInfo"/>.</summary>
    [Column(TypeName = "TEXT")]
    public string? SystemInfoJson { get; set; }

    // ==================== ROUTER-SIDE SETTINGS ====================

    /// <summary>
    /// Host name/IP the router uses to reach backends on this machine. Null = the address the
    /// agent connected from (<see cref="LastRemoteAddress"/>).
    /// </summary>
    [MaxLength(256)]
    public string? HostOverride { get; set; }

    /// <summary>Where engines are installed on the agent. Null = <c>&lt;data dir&gt;/engines</c>.</summary>
    [MaxLength(1024)]
    public string? EngineRoot { get; set; }

    /// <summary>Where models are stored on the agent. Null = <c>&lt;data dir&gt;/models</c>.</summary>
    [MaxLength(1024)]
    public string? ModelRoot { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenAt { get; set; }
    public DateTime? LastConnectedAt { get; set; }

    [MaxLength(2048)]
    public string? LastError { get; set; }

    /// <summary>The host the router connects to for this agent's backends.</summary>
    [NotMapped]
    public string? EffectiveHost => string.IsNullOrWhiteSpace(HostOverride) ? LastRemoteAddress : HostOverride.Trim();

    [NotMapped]
    public bool IsWindows => string.Equals(Os, "windows", StringComparison.OrdinalIgnoreCase);

    public ICollection<ServerInstance> Servers { get; set; } = new List<ServerInstance>();
}

/// <summary>
/// Router-wide agent settings (single row, Id = 1): the enrollment token new agents present on
/// first contact, and the URL install commands tell agents to reach the router on.
/// </summary>
[Table("AgentSettings")]
public class AgentSettings
{
    [Key]
    public int Id { get; set; } = 1;

    /// <summary>Shared secret embedded in the install command. Rotating it doesn't affect agents already enrolled.</summary>
    [MaxLength(128)]
    public string EnrollmentToken { get; set; } = string.Empty;

    /// <summary>
    /// Router URL as seen from agent machines (e.g. <c>http://router.lan:5000</c>). Null = the
    /// URL the admin is browsing with.
    /// </summary>
    [MaxLength(512)]
    public string? PublicUrl { get; set; }
}
