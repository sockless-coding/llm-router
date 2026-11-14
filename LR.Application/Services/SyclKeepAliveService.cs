using System.Diagnostics;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;

namespace LR.Application.Services;

public enum SyclKeepAliveState
{
    /// <summary>Not Windows — the VRAM-drop problem and the keep-alive are Windows-only.</summary>
    Unsupported,
    /// <summary>Off, or nothing on this machine needs it.</summary>
    Idle,
    Running,
    /// <summary>A keep-alive the router didn't start (e.g. a server's companion app) is already running.</summary>
    RunningExternally,
    Failed,
    /// <summary>Stopped from the Devices page; not restarted until started again (or the router restarts).</summary>
    Stopped,
}

public sealed record SyclKeepAliveStatus(
    SyclKeepAliveState State,
    string Message,
    int? ProcessId = null,
    DateTimeOffset? Since = null,
    IReadOnlyList<string>? LogTail = null);

/// <summary>
/// Runs at most one SYCL keep-alive process for the whole machine when
/// <see cref="SyclKeepAliveSettings.Mode"/> asks for it, and re-checks every minute so plugging
/// in or removing a monitor (or enabling a card) starts or stops it. Windows only.
///
/// The process is deliberately left running when the router shuts down — servers keep their
/// models loaded across router restarts, so their VRAM still needs keeping alive — and a
/// keep-alive found running from the same executable at startup is adopted rather than doubled.
/// One running from anywhere else (e.g. an existing per-server companion app) is respected and
/// never killed.
/// </summary>
public sealed class SyclKeepAliveService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StartupCheck = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private const int LogTailLines = 15;

    private readonly ISyclKeepAliveSettingsService _settings;
    private readonly IComputeDeviceService _devices;
    private readonly ILogger<SyclKeepAliveService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "data", "sycl-keepalive.log");
    // What the running keep-alive was started with, so one adopted after a router restart is
    // only kept when it still matches (e.g. the same --device cards).
    private readonly string _configPath = Path.Combine(AppContext.BaseDirectory, "data", "sycl-keepalive.config");

    // The process we own: cmd.exe wrapping the keep-alive when we started it, or the keep-alive
    // itself when adopted from a previous router run.
    private Process? _process;
    private string? _processConfig;
    private DateTimeOffset _lastStartAttempt = DateTimeOffset.MinValue;

    public SyclKeepAliveStatus Status { get; private set; } = new(SyclKeepAliveState.Idle, "Not checked yet.");

    public SyclKeepAlivePlan? LastPlan { get; private set; }

    public string BundledExecutablePath { get; } = SyclKeepAlivePlanner.BundledExecutablePath(AppContext.BaseDirectory);

    public SyclKeepAliveService(ISyclKeepAliveSettingsService settings, IComputeDeviceService devices, ILogger<SyclKeepAliveService> logger)
    {
        _settings = settings;
        _devices = devices;
        _logger = logger;
    }

    /// <summary>Re-evaluates immediately (e.g. after the settings changed) and returns the new status.</summary>
    public async Task<SyclKeepAliveStatus> ReevaluateAsync(CancellationToken ct = default)
    {
        await EvaluateAsync(force: true, ct);
        return Status;
    }

    // Manual stop from the Devices page. In memory on purpose: Mode = Off is the persistent switch,
    // so a router restart goes back to what the Mode says.
    private volatile bool _stopped;

    /// <summary>
    /// Stops the keep-alive the router runs and keeps the periodic check from starting it again
    /// until <see cref="StartKeepAliveAsync"/>. A keep-alive the router didn't start is left alone.
    /// </summary>
    public async Task<SyclKeepAliveStatus> StopKeepAliveAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _stopped = true;
            StopOwned();
            Status = StoppedStatus();
        }
        finally
        {
            _gate.Release();
        }

        return Status;
    }

    /// <summary>Clears a manual stop and starts the keep-alive now, if the Mode and devices call for it.</summary>
    public Task<SyclKeepAliveStatus> StartKeepAliveAsync(CancellationToken ct = default)
    {
        _stopped = false;
        return ReevaluateAsync(ct);
    }

    private static readonly TimeSpan EnableSettleDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs a device enable/disable and then re-checks, so the keep-alive follows the change
    /// immediately instead of on the next minute tick. With <paramref name="releaseGpu"/> (disabling
    /// a SYCL GPU) the keep-alive is stopped first: it holds a Level Zero context on the card, and
    /// disabling a device that's in use can fail or need a reboot. The periodic check is held off
    /// for the whole change so it can't restart the keep-alive mid-way. A just-enabled card can take
    /// a moment before SYCL sees it, so a failed start after an enable is retried once.
    /// </summary>
    public async Task<T> RunDeviceChangeAsync<T>(bool releaseGpu, bool enabling, Func<Task<T>> change, CancellationToken ct = default)
    {
        T result;
        await _gate.WaitAsync(ct);
        try
        {
            if (releaseGpu)
                StopOwned();
            result = await change();
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await EvaluateAsync(force: true, ct);
            if (enabling && Status.State == SyclKeepAliveState.Failed)
            {
                await Task.Delay(EnableSettleDelay, ct);
                await EvaluateAsync(force: true, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The device change itself already happened; the minute tick will catch up.
            _logger.LogWarning(ex, "SYCL keep-alive re-check after a device change failed.");
        }

        return result;
    }

    private static SyclKeepAliveStatus StoppedStatus() => new(SyclKeepAliveState.Stopped,
        "Stopped manually. The router won't start it again until you press Start, or until the router restarts.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            Status = new(SyclKeepAliveState.Unsupported, "The SYCL keep-alive is only needed (and only supported) on Windows.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateAsync(force: false, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "SYCL keep-alive check failed; continuing.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task EvaluateAsync(bool force, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return;

        await _gate.WaitAsync(ct);
        try
        {
            var settings = await _settings.GetAsync(ct);
            var devices = await _devices.GetDevicesAsync(ct);
            var plan = SyclKeepAlivePlanner.Evaluate(settings.Mode, devices);
            LastPlan = plan;

            if (_stopped)
            {
                StopOwned();
                Status = StoppedStatus();
                return;
            }

            if (!plan.ShouldRun)
            {
                StopOwned();
                Status = new(SyclKeepAliveState.Idle, plan.Reason);
                return;
            }

            var exePath = Path.GetFullPath(settings.ExecutablePath ?? BundledExecutablePath);
            var config = string.Join("\n", new[] { exePath, settings.EnvironmentSetupCommand }.Concat(plan.Arguments));

            if (_process is not null)
            {
                if (!HasExited(_process) && _processConfig == config)
                {
                    Status = Status with { State = SyclKeepAliveState.Running, Message = plan.Reason };
                    return;
                }

                if (HasExited(_process))
                {
                    var exit = SafeExitCode(_process);
                    _logger.LogWarning("SYCL keep-alive exited ({Exit}).", exit);
                    Status = new(SyclKeepAliveState.Failed,
                        $"Keep-alive stopped: {(exit is { } code ? SyclKeepAlivePlanner.DescribeExit(code) : "unknown exit code")}.",
                        LogTail: ReadLogTail());
                }
                StopOwned();
            }

            // A keep-alive already running elsewhere covers every Intel card, so don't add a second.
            var existing = FindRunningKeepAlives(exePath);
            var adopted = existing.Own;
            if (adopted is not null && ReadConfigFile() != config)
            {
                // Ours from a previous run, but started for different cards or settings.
                _process = adopted;
                StopOwned();
                adopted = null;
            }
            if (adopted is not null)
            {
                _process = adopted;
                _processConfig = config;
                Status = new(SyclKeepAliveState.Running, plan.Reason + " (adopted from a previous run)", adopted.Id, SafeStartTime(adopted), ReadLogTail());
                DisposeAll(existing.External);
                return;
            }
            if (existing.External.Count > 0)
            {
                var other = existing.External[0];
                Status = new(SyclKeepAliveState.RunningExternally,
                    $"A keep-alive the router didn't start is already running (PID {other.Id}), so the router isn't starting another.",
                    other.Id);
                DisposeAll(existing.External);
                return;
            }

            if (!File.Exists(exePath))
            {
                Status = new(SyclKeepAliveState.Failed, $"Keep-alive executable not found: {exePath}");
                return;
            }

            // Don't hammer a failing start (e.g. oneAPI missing) — retry at most every RetryDelay
            // unless the user just changed the settings.
            if (!force && Status.State == SyclKeepAliveState.Failed && DateTimeOffset.UtcNow - _lastStartAttempt < RetryDelay)
                return;

            await StartAsync(exePath, plan.Arguments, settings.EnvironmentSetupCommand, config, plan.Reason, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartAsync(string exePath, IReadOnlyList<string> arguments, string environmentSetupCommand, string config, string reason, CancellationToken ct)
    {
        _lastStartAttempt = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = SyclKeepAlivePlanner.BuildCommandLine(exePath, arguments, environmentSetupCommand, _logPath),
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // When set (some dev shells and agents do), cmd stops resolving bare names from the
        // current directory, and setvars.bat's "call vars.bat" fails without loading oneAPI.
        startInfo.Environment.Remove("NoDefaultCurrentDirectoryInExePath");

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("cmd.exe didn't start.");
        }
        catch (Exception ex)
        {
            Status = new(SyclKeepAliveState.Failed, $"Couldn't start the keep-alive: {ex.Message}");
            return;
        }

        // setvars + SYCL device discovery takes a few seconds; a missing runtime fails within that.
        try { await process.WaitForExitAsync(ct).WaitAsync(StartupCheck, ct); }
        catch (TimeoutException) { }

        if (process.HasExited)
        {
            var message = $"Keep-alive exited right after starting: {SyclKeepAlivePlanner.DescribeExit(process.ExitCode)}.";
            _logger.LogWarning("SYCL keep-alive failed to start: {Message}", message);
            Status = new(SyclKeepAliveState.Failed, message, LogTail: ReadLogTail());
            process.Dispose();
            return;
        }

        _process = process;
        _processConfig = config;
        try { File.WriteAllText(_configPath, config); } catch { /* only used to adopt it later */ }
        _logger.LogInformation("Started SYCL keep-alive (PID {Pid}): {Reason}", process.Id, reason);
        Status = new(SyclKeepAliveState.Running, reason, process.Id, DateTimeOffset.Now, ReadLogTail());
    }

    private void StopOwned()
    {
        if (_process is null)
            return;

        try
        {
            if (!_process.HasExited)
            {
                // entireProcessTree: the started process is cmd.exe with the keep-alive as its child.
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
                _logger.LogInformation("Stopped SYCL keep-alive.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop the SYCL keep-alive.");
        }

        _process.Dispose();
        _process = null;
        _processConfig = null;
    }

    /// <summary>
    /// Running keep-alives by process name — the configured executable's name plus the bundled
    /// one's. <c>Own</c> is one started from <paramref name="exePath"/> (a previous router run's).
    /// </summary>
    private static (Process? Own, List<Process> External) FindRunningKeepAlives(string exePath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFileNameWithoutExtension(exePath),
            Path.GetFileNameWithoutExtension(SyclKeepAlivePlanner.BundledExecutableName),
        };

        Process? own = null;
        var external = new List<Process>();
        foreach (var process in names.SelectMany(Process.GetProcessesByName))
        {
            string? path = null;
            try { path = process.MainModule?.FileName; }
            catch { /* another user's process when not elevated — treat it as external */ }

            if (own is null && path is not null && string.Equals(Path.GetFullPath(path), exePath, StringComparison.OrdinalIgnoreCase))
                own = process;
            else
                external.Add(process);
        }
        return (own, external);
    }

    private string? ReadConfigFile()
    {
        try { return File.Exists(_configPath) ? File.ReadAllText(_configPath) : null; }
        catch { return null; }
    }

    private IReadOnlyList<string>? ReadLogTail()
    {
        try
        {
            if (!File.Exists(_logPath))
                return null;
            using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                lines.Enqueue(line);
                if (lines.Count > LogTailLines)
                    lines.Dequeue();
            }
            return lines.ToList();
        }
        catch
        {
            return null;
        }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private static int? SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return null; }
    }

    private static DateTimeOffset? SafeStartTime(Process process)
    {
        try { return process.StartTime; }
        catch { return null; }
    }

    private static void DisposeAll(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
            process.Dispose();
    }
}
