using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LR.Core.Models;

/// <summary>When the router runs its bundled SYCL keep-alive (see <see cref="SyclKeepAliveSettings"/>).</summary>
public enum SyclKeepAliveMode
{
    /// <summary>Never run it (default — the feature is opt-in).</summary>
    Off = 0,

    /// <summary>Run it while at least one enabled SYCL-capable Intel GPU has no monitor attached.</summary>
    Headless = 1,

    /// <summary>Run it whenever an enabled SYCL-capable Intel GPU is present, monitor or not.</summary>
    Always = 2,
}

/// <summary>
/// Settings for the router-wide SYCL keep-alive: one small process that submits a no-op SYCL
/// kernel every few seconds so a Windows Intel GPU without a monitor doesn't drop its VRAM after
/// about a minute idle. A single instance covers every Intel card, so this is a machine-level
/// setting rather than a per-server companion app. Single-row table, edited from the Devices page.
/// </summary>
[Table("SyclKeepAliveSettings")]
public class SyclKeepAliveSettings
{
    public const string DefaultEnvironmentSetupCommand =
        "\"C:\\Program Files (x86)\\Intel\\oneAPI\\setvars.bat\" intel64 --force";

    /// <summary>Singleton row — this table only ever holds one record, with Id fixed at 1.</summary>
    [Key]
    public int Id { get; set; } = 1;

    public SyclKeepAliveMode Mode { get; set; } = SyclKeepAliveMode.Off;

    /// <summary>
    /// Run before the keep-alive in the same shell so the oneAPI SYCL runtime (sycl9.dll and
    /// friends) is on PATH. Empty when the runtime is already reachable without it.
    /// </summary>
    [MaxLength(1024)]
    public string EnvironmentSetupCommand { get; set; } = DefaultEnvironmentSetupCommand;

    /// <summary>A keep-alive executable to use instead of the bundled one. Null uses the bundled build.</summary>
    [MaxLength(1024)]
    public string? ExecutablePath { get; set; }
}
