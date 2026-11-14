using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

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
    /// <summary>A keep-alive this router can't manage is running (e.g. a server's companion app).</summary>
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
/// models loaded across router restarts, so their VRAM still needs keeping alive. Whichever router
/// started it is recorded machine-wide under <c>%ProgramData%\LLM Router</c>, so the next router to
/// run takes it over (to keep, retarget or stop it) even if it's a different install, such as a
/// <c>dotnet run</c> build after the service. Only one router runs at a time, so there is no
/// arbitration between live instances. A keep-alive no router started (e.g. a per-server companion
/// app) is respected and never killed.
/// </summary>
public sealed class SyclKeepAliveService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StartupCheck = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EnableSettleDelay = TimeSpan.FromSeconds(5);
    private const int LogTailLines = 15;

    private static readonly string SharedDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LLM Router");

    private readonly ISyclKeepAliveSettingsService _settings;
    private readonly IComputeDeviceService _devices;
    private readonly ILogger<SyclKeepAliveService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _logPath = Path.Combine(SharedDirectory, "sycl-keepalive.log");
    private readonly string _recordPath = Path.Combine(SharedDirectory, "sycl-keepalive.json");
    private static readonly string StagingRoot = Path.Combine(SharedDirectory, "sycl-keepalive");
    // Content hash of the bundled executable, naming its staged copy's folder.
    private string? _bundledHash;

    // The process we manage: cmd.exe wrapping the keep-alive (started by us, or by an earlier
    // router run and taken over from the shared record), or a bare keep-alive found by path.
    private Process? _process;
    // What _process was started with; null when unknown, which forces a restart with ours.
    private string? _processConfig;
    private DateTimeOffset _lastStartAttempt = DateTimeOffset.MinValue;

    // Manual stop from the Devices page. In memory on purpose: Mode = Off is the persistent switch,
    // so a router restart goes back to what the Mode says.
    private volatile bool _stopped;

    public SyclKeepAliveStatus Status { get; private set; } = new(SyclKeepAliveState.Idle, "Not checked yet.");

    public SyclKeepAlivePlan? LastPlan { get; private set; }

    public string BundledExecutablePath { get; } = SyclKeepAlivePlanner.BundledExecutablePath(AppContext.BaseDirectory);

    /// <summary>Which router started a keep-alive, and with what — shared by every install on the machine.</summary>
    private sealed record OwnerRecord(int Pid, DateTime StartTimeUtc, string Config, string StartedBy);

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

    /// <summary>
    /// Stops the keep-alive the router runs and keeps the periodic check from starting it again
    /// until <see cref="StartKeepAliveAsync"/>. A keep-alive no router started is left alone.
    /// </summary>
    public async Task<SyclKeepAliveStatus> StopKeepAliveAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _stopped = true;
            if (TakeOverRecorded() is { } blocked)
                Status = blocked;
            else
                Status = StopOwned() ? StoppedStatus() : CannotStopStatus();
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
            if (releaseGpu && TakeOverRecorded() is null && !StopOwned())
                _logger.LogWarning("Couldn't release the GPU from the SYCL keep-alive before disabling it; continuing.");
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

    private SyclKeepAliveStatus CannotStopStatus() => new(SyclKeepAliveState.Failed,
        $"Couldn't stop the keep-alive (PID {SafeId(_process)}). It was likely started by the router service as " +
        "another user — run this router elevated, or stop it from the instance that started it.", SafeId(_process));

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

            // First take over a keep-alive an earlier router run (possibly another install) left
            // running, so the decisions below — including stopping it — apply to it too.
            if (TakeOverRecorded() is { } blocked)
            {
                Status = blocked;
                return;
            }

            if (_stopped || !plan.ShouldRun)
            {
                Status = !StopOwned() ? CannotStopStatus()
                    : _stopped ? StoppedStatus()
                    : new(SyclKeepAliveState.Idle, plan.Reason);
                return;
            }

            var exePath = settings.ExecutablePath is { } custom ? Path.GetFullPath(custom) : StageBundledExecutable();
            var config = string.Join("\n", new[] { exePath, settings.EnvironmentSetupCommand }.Concat(plan.Arguments));

            if (_process is not null)
            {
                if (!HasExited(_process) && _processConfig == config)
                {
                    Status = Status.State == SyclKeepAliveState.Running
                        ? Status with { Message = plan.Reason }
                        : new(SyclKeepAliveState.Running, plan.Reason, _process.Id, SafeStartTime(_process), ReadLogTail());
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

                // Exited, or running with other cards/settings (e.g. taken over from another install).
                if (!StopOwned())
                {
                    Status = CannotStopStatus();
                    return;
                }
            }

            // No record, but one running from our own executable is ours too (e.g. the record was
            // lost); its arguments are unknown, so it gets restarted with ours.
            var existing = FindRunningKeepAlives(exePath, BundledExecutablePath);
            if (existing.Own is { } stray)
            {
                _process = stray;
                _processConfig = null;
                if (!StopOwned())
                {
                    DisposeAll(existing.External);
                    Status = CannotStopStatus();
                    return;
                }
            }

            // A keep-alive no router started covers every Intel card already, so don't add a second.
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
        EnsureSharedDirectory();

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
        WriteRecord(new OwnerRecord(process.Id, process.StartTime.ToUniversalTime(), config, AppContext.BaseDirectory));
        _logger.LogInformation("Started SYCL keep-alive (PID {Pid}): {Reason}", process.Id, reason);
        RemoveStaleStagedCopies(exePath);
        Status = new(SyclKeepAliveState.Running, reason, process.Id, DateTimeOffset.Now, ReadLogTail());
    }

    /// <summary>
    /// Kills the managed keep-alive (if any) and clears the shared record. Returns false when it's
    /// still running because it couldn't be killed — then it stays managed, so no second one starts.
    /// </summary>
    private bool StopOwned()
    {
        if (_process is null)
            return true;

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

        if (StillRunning(_process))
            return false;

        _process.Dispose();
        _process = null;
        _processConfig = null;
        DeleteRecord();
        return true;
    }

    /// <summary>
    /// When nothing is managed yet, takes over the keep-alive recorded in the shared record if that
    /// exact process (PID and start time) is still running. Returns a status to show instead of
    /// continuing when the recorded process exists but can't be inspected — typically started by
    /// the router service as SYSTEM while this instance runs unelevated.
    /// </summary>
    private SyclKeepAliveStatus? TakeOverRecorded()
    {
        if (_process is not null || ReadRecord() is not { } record)
            return null;

        Process process;
        try
        {
            process = Process.GetProcessById(record.Pid);
        }
        catch (ArgumentException)
        {
            DeleteRecord(); // that process is gone
            return null;
        }

        try
        {
            if (!process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - record.StartTimeUtc).TotalSeconds) < 2)
            {
                _process = process;
                _processConfig = record.Config;
                if (!string.Equals(record.StartedBy, AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
                    _logger.LogInformation("Took over the SYCL keep-alive (PID {Pid}) started by the router at {Other}.", record.Pid, record.StartedBy);
                return null;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            process.Dispose();
            return new(SyclKeepAliveState.RunningExternally,
                $"The keep-alive started by the router at {record.StartedBy} (PID {record.Pid}) is running as another user, " +
                "so this instance can't manage it. Run this router elevated to take it over.", record.Pid);
        }

        // Same PID, different process: the recorded keep-alive is gone and the PID was reused.
        process.Dispose();
        DeleteRecord();
        return null;
    }

    /// <summary>
    /// Running keep-alives by process name — the configured executable's name plus the bundled
    /// one's. <c>Own</c> is one started from <paramref name="exePath"/>, or from the install folder's
    /// bundled copy (where earlier versions ran it).
    /// </summary>
    private static (Process? Own, List<Process> External) FindRunningKeepAlives(string exePath, string bundledPath)
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

            if (own is null && path is not null && (string.Equals(Path.GetFullPath(path), exePath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFullPath(path), Path.GetFullPath(bundledPath), StringComparison.OrdinalIgnoreCase)))
                own = process;
            else
                external.Add(process);
        }
        return (own, external);
    }

    // --- Staged copy of the bundled executable ---

    /// <summary>
    /// Returns the path to run the bundled keep-alive from: a copy under the shared folder, not the
    /// install folder. The keep-alive outlives the router on purpose, and running it from
    /// <c>bin\</c> locks the file so the next build (or an update of the install) can't replace it.
    /// The copy's folder is named by content hash, so a new keep-alive build gets a new path; its
    /// config then differs and the running one is replaced. Falls back to the install folder's copy.
    /// </summary>
    private string StageBundledExecutable()
    {
        if (!File.Exists(BundledExecutablePath))
            return BundledExecutablePath; // reported as not found by the caller

        try
        {
            _bundledHash ??= Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(BundledExecutablePath)))[..12].ToLowerInvariant();
            var directory = Path.Combine(StagingRoot, _bundledHash);
            var staged = Path.Combine(directory, SyclKeepAlivePlanner.BundledExecutableName);
            if (!File.Exists(staged))
            {
                EnsureSharedDirectory();
                Directory.CreateDirectory(directory);
                var temp = staged + ".tmp";
                File.Copy(BundledExecutablePath, temp, overwrite: true);
                File.Move(temp, staged, overwrite: true);
            }
            return staged;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't copy the bundled SYCL keep-alive to {Dir}; running it from the install folder.", StagingRoot);
            return BundledExecutablePath;
        }
    }

    /// <summary>Best effort: deletes staged copies other than the one now running (one still in use just stays).</summary>
    private static void RemoveStaleStagedCopies(string exePath)
    {
        var current = Path.GetDirectoryName(exePath);
        if (current is null || !string.Equals(Path.GetDirectoryName(current), StagingRoot, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(StagingRoot))
            {
                if (string.Equals(directory, current, StringComparison.OrdinalIgnoreCase))
                    continue;
                try { Directory.Delete(directory, recursive: true); }
                catch { /* in use or not ours to delete */ }
            }
        }
        catch { /* best effort */ }
    }

    // --- Shared record (%ProgramData%\LLM Router) ---

    /// <summary>
    /// Creates the shared folder so every account a router may run as — the service as SYSTEM, a
    /// dev build as the signed-in user — can replace the record and log the other one wrote.
    /// </summary>
    private void EnsureSharedDirectory()
    {
        if (Directory.Exists(SharedDirectory))
            return;

        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException();
            var security = new DirectorySecurity();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(SharedDirectory).Create(security);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Couldn't create {Dir} with shared permissions; using the defaults.", SharedDirectory);
            Directory.CreateDirectory(SharedDirectory);
        }
    }

    private OwnerRecord? ReadRecord()
    {
        try { return File.Exists(_recordPath) ? JsonSerializer.Deserialize<OwnerRecord>(File.ReadAllText(_recordPath)) : null; }
        catch { return null; }
    }

    private void WriteRecord(OwnerRecord record)
    {
        try { File.WriteAllText(_recordPath, JsonSerializer.Serialize(record)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Couldn't write {Path}; another router install won't be able to take this keep-alive over.", _recordPath); }
    }

    private void DeleteRecord()
    {
        try { File.Delete(_recordPath); }
        catch { /* best effort — a stale record is rejected by its start time anyway */ }
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

    /// <summary>
    /// Stricter than <see cref="HasExited"/>: when the process can't be queried (access denied),
    /// it counts as running as long as its PID still exists, so a keep-alive this instance isn't
    /// allowed to kill is never mistaken for stopped (which would start a second one).
    /// </summary>
    private static bool StillRunning(Process process)
    {
        try { return !process.HasExited; }
        catch
        {
            try { using var _ = Process.GetProcessById(process.Id); return true; }
            catch { return false; }
        }
    }

    private static int? SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return null; }
    }

    private static int? SafeId(Process? process)
    {
        try { return process?.Id; }
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
