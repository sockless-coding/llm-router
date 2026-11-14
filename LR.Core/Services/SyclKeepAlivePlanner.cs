using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>Whether the SYCL keep-alive should run right now, and why.</summary>
/// <param name="ShouldRun">True when a keep-alive process is wanted.</param>
/// <param name="Reason">Explains the decision on the Devices page.</param>
/// <param name="Targets">The enabled SYCL-capable GPUs that were considered.</param>
/// <param name="Arguments">Keep-alive arguments: <c>--device</c> per card to keep alive, or none for every GPU.</param>
public sealed record SyclKeepAlivePlan(bool ShouldRun, string Reason, IReadOnlyList<ComputeDevice> Targets, IReadOnlyList<string> Arguments)
{
    public SyclKeepAlivePlan(bool shouldRun, string reason, IReadOnlyList<ComputeDevice> targets)
        : this(shouldRun, reason, targets, Array.Empty<string>()) { }
}

/// <summary>
/// Pure decision logic for the router-wide SYCL keep-alive, kept apart from the process
/// supervision so it can be unit-tested.
/// </summary>
public static class SyclKeepAlivePlanner
{
    public const string BundledExecutableName = "sycl-keepalive.exe";

    /// <summary>Where the build copies the bundled keep-alive, relative to the app's base directory.</summary>
    public static string BundledExecutablePath(string baseDirectory) =>
        Path.Combine(baseDirectory, "Tools", "sycl-keepalive", BundledExecutableName);

    public static SyclKeepAlivePlan Evaluate(SyclKeepAliveMode mode, IReadOnlyList<ComputeDevice> devices)
    {
        var targets = devices.Where(d => d.IsSyclGpu).ToList();

        if (mode == SyclKeepAliveMode.Off)
            return new(false, "Keep-alive is off.", targets);

        if (targets.Count == 0)
            return new(false, "No enabled SYCL-capable Intel GPU detected.", targets);

        if (mode == SyclKeepAliveMode.Always)
            return new(true, "Mode is Always.", targets);

        // An unknown display state counts as headless: the user opted in, and running one
        // idle keep-alive needlessly costs far less than losing a loaded model.
        var headless = targets.Where(d => d.HasDisplayAttached != true).ToList();
        if (headless.Count == 0)
            return new(false, "Every SYCL GPU has a monitor attached.", targets);

        // Aim it at just the headless cards when every one has a known PCI address; otherwise
        // keep every GPU alive rather than risk missing the one that needs it.
        var arguments = headless.All(d => d.PciAddress is not null)
            ? headless.SelectMany(d => new[] { "--device", d.PciAddress! }).ToList()
            : new List<string>();
        return new(true, $"No monitor on {string.Join(", ", headless.Select(d => d.Name))}.", targets, arguments);
    }

    /// <summary>
    /// The <c>cmd.exe</c> argument string that loads the environment (when set), then runs the
    /// keep-alive, with all output appended to <paramref name="logPath"/>. Redirecting to a file
    /// rather than a pipe lets the process keep running across router restarts, like servers do.
    /// <c>/s</c> makes cmd strip only the outer quote pair, so the inner quoting survives as written.
    /// Each command gets its own redirect rather than sharing a <c>( ... )</c> group, so a
    /// <c>)</c> in a user-entered setup command (<c>Program Files (x86)</c>) can't end a group early.
    /// </summary>
    public static string BuildCommandLine(string executablePath, IReadOnlyList<string> arguments, string? environmentSetupCommand, string logPath)
    {
        var log = $"\"{logPath}\"";
        var args = arguments.Count > 0 ? " " + LR.Core.Wrapper.WindowsCommandLine.Join(arguments) : "";
        var run = $"\"{executablePath}\"{args} >> {log} 2>&1";
        var body = string.IsNullOrWhiteSpace(environmentSetupCommand)
            ? run
            : $"call {environmentSetupCommand.Trim()} > {log} 2>&1 & {run}";
        return $"/d /s /c \"{body}\"";
    }

    /// <summary>Turns well-known exit codes into a hint for the Devices page.</summary>
    public static string DescribeExit(int exitCode) => unchecked((uint)exitCode) switch
    {
        0xC0000135 => "a required DLL (e.g. sycl9.dll) wasn't found — check the oneAPI setup command",
        3 => "no GPU matched — the keep-alive couldn't see the selected card(s); see its output",
        0xC0000409 => "it aborted — usually no SYCL GPU was visible to it (check the device is enabled and the Intel driver is installed)",
        0xC0000139 =>"a DLL entry point is missing — the oneAPI runtime is likely a different version than the keep-alive was built against",
        _ => $"exit code {exitCode}",
    };
}
