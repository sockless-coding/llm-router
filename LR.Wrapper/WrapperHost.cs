using System.Diagnostics;

using LR.Core.Wrapper;

namespace LR.Wrapper;

/// <summary>
/// Owns the companion process and main server process for one server instance, independent of
/// which (if any) router connection is currently attached. Deliberately engine-agnostic: it only
/// knows how to run an executable, run a companion alongside it, and stream raw output — all
/// llama.cpp-specific interpretation (readiness markers, timing parsing) lives router-side,
/// fed from the <see cref="OutputLineEvent"/>s this class emits.
/// </summary>
public sealed class WrapperHost
{
    private const int MaxBacklogLines = 500;

    private readonly List<string> _outputBacklog = new();
    private readonly object _backlogLock = new();

    private volatile WrapperPipeConnection? _currentConnection;

    private Process? _serverProcess;
    private Process? _companionProcess;
    private CancellationTokenSource? _outputCts;
    private volatile bool _suppressNextExitEvent;
    private string? _pendingTempBatchToCleanup;
    private int? _lastKnownPort;

    private string? _companionAppPath;
    private string? _companionEnvironmentSetupCommand;

    public bool ShutdownRequested { get; private set; }

    public void SetConnection(WrapperPipeConnection connection) => _currentConnection = connection;

    public void ClearConnection() => _currentConnection = null;

    public HelloEvent BuildHello() => new()
    {
        WrapperPid = Environment.ProcessId,
        ServerPid = _serverProcess is { HasExited: false } ? _serverProcess.Id : null,
        ServerRunning = _serverProcess is { HasExited: false },
        CompanionRunning = _companionProcess is { HasExited: false },
        Port = _lastKnownPort,
        RecentOutputBacklog = SnapshotBacklog(),
    };

    public async Task HandleMessageAsync(WrapperPipeConnection connection, WrapperMessage message)
    {
        switch (message)
        {
            case StartServerCommand cmd:
                try
                {
                    await StartServerAsync(cmd);
                    await connection.SendAsync(new CommandAckEvent { Success = true });
                }
                catch (Exception ex)
                {
                    await connection.SendAsync(new CommandAckEvent { Success = false, Error = ex.Message });
                }
                break;

            case StopCommand cmd:
                await StopServerAsync(cmd.StopCompanion);
                await connection.SendAsync(new CommandAckEvent { Success = true });
                if (cmd.ShutdownWrapper)
                    ShutdownRequested = true;
                break;

            case PingCommand:
                await connection.SendAsync(BuildHello());
                break;
        }
    }

    /// <summary>
    /// Stops everything this host owns. Called on a Stop{ShutdownWrapper:true} command and on
    /// the wrapper's own graceful shutdown (Ctrl+C/SIGTERM) — in both cases nothing should be
    /// left running with no wrapper left to manage it.
    /// </summary>
    public async Task StopEverythingAsync()
    {
        await StopServerProcessAsync();
        await StopCompanionAppAsync();
    }

    private async Task StartServerAsync(StartServerCommand cmd)
    {
        await EnsureCompanionRunningAsync(cmd.CompanionAppPath, cmd.EnvironmentSetupCommand);
        await StartServerProcessAsync(cmd.ExecutablePath, cmd.Arguments, cmd.WorkingDirectory, cmd.EnvironmentSetupCommand, cmd.Port);
    }

    private async Task StopServerAsync(bool stopCompanion)
    {
        await StopServerProcessAsync();
        if (stopCompanion)
            await StopCompanionAppAsync();
    }

    // --- Main server process ---

    private async Task StartServerProcessAsync(string executablePath, string arguments, string? workingDirectory, string? environmentSetupCommand, int? port)
    {
        // Idempotent "ensure": stop whatever main server is currently running (this is an
        // intentional stop, not a crash — suppressed from ProcessExitedEvent) before swapping in
        // the new one.
        await StopServerProcessAsync();

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
        };

        if (!string.IsNullOrEmpty(environmentSetupCommand))
        {
            _pendingTempBatchToCleanup = await CreateTempBatchScriptAsync(executablePath, arguments, environmentSetupCommand);
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = "/c \"" + _pendingTempBatchToCleanup + "\"";
        }

        _lastKnownPort = port;
        _serverProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _serverProcess.Exited += (sender, eventArgs) =>
        {
            var exitCode = ((Process)sender!).ExitCode;
            _ = OnServerExitedAsync(exitCode);
        };

        _serverProcess.Start();

        _outputCts = new CancellationTokenSource();
        var stdoutToken = _outputCts.Token;
        var stderrToken = _outputCts.Token;
        var process = _serverProcess;
        _ = Task.Run(() => PumpOutput(process, process.StandardOutput, WrapperOutputStream.Stdout, stdoutToken));
        _ = Task.Run(() => PumpOutput(process, process.StandardError, WrapperOutputStream.Stderr, stderrToken));

        await BroadcastAsync(new ProcessStartedEvent { ServerPid = _serverProcess.Id });
    }

    // Uses synchronous ReadLine() rather than async stream reads to avoid InvalidOperationException
    // from concurrent async operations on the same stream (stdout/stderr are read on separate
    // threads concurrently) — same rationale as the process manager this was moved from.
    private void PumpOutput(Process process, StreamReader reader, WrapperOutputStream stream, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = reader.ReadLine();
                if (line is null) break;

                AddToBacklog(line);
                _ = BroadcastAsync(new OutputLineEvent { Stream = stream, Line = line });
            }
        }
        catch
        {
            // Stream closed because the process exited — OnServerExitedAsync handles notifying the router.
        }
    }

    private async Task OnServerExitedAsync(int exitCode)
    {
        _outputCts?.Cancel();
        CleanupPendingTempBatch();

        bool suppress = _suppressNextExitEvent;
        _suppressNextExitEvent = false;

        if (!suppress)
            await BroadcastAsync(new ProcessExitedEvent { ExitCode = exitCode });
    }

    private async Task StopServerProcessAsync()
    {
        _outputCts?.Cancel();

        if (_serverProcess is not null && !_serverProcess.HasExited)
        {
            _suppressNextExitEvent = true;
            try
            {
                _serverProcess.Kill();
                await _serverProcess.WaitForExitAsync();
            }
            catch { /* best effort */ }
        }

        _serverProcess?.Dispose();
        _serverProcess = null;
        _lastKnownPort = null;
        CleanupPendingTempBatch();
    }

    private async Task<string> CreateTempBatchScriptAsync(string executablePath, string arguments, string environmentSetupCommand)
    {
        string tempBatchPath = Path.Combine(Path.GetTempPath(), $"llm-router-init-{Guid.NewGuid():N}.bat");
        var lines = new List<string>
        {
            "@echo off",
            environmentSetupCommand,
            $"call \"{executablePath}\" {arguments}",
        };
        await File.WriteAllLinesAsync(tempBatchPath, lines);
        return tempBatchPath;
    }

    private void CleanupPendingTempBatch()
    {
        if (!string.IsNullOrEmpty(_pendingTempBatchToCleanup) && File.Exists(_pendingTempBatchToCleanup))
        {
            try { File.Delete(_pendingTempBatchToCleanup); } catch { /* best effort */ }
        }
        _pendingTempBatchToCleanup = null;
    }

    // --- Companion app ---

    private async Task EnsureCompanionRunningAsync(string? companionAppPath, string? environmentSetupCommand)
    {
        if (string.IsNullOrEmpty(companionAppPath))
        {
            await StopCompanionAppAsync();
            return;
        }

        bool alreadyRunning = _companionProcess is { HasExited: false };
        bool configChanged = _companionAppPath != companionAppPath || _companionEnvironmentSetupCommand != environmentSetupCommand;

        if (alreadyRunning && !configChanged)
            return; // idempotent — leave the companion (and its GPU/VRAM state) alone

        if (alreadyRunning)
            await StopCompanionAppAsync();

        await StartCompanionAppAsync(companionAppPath, environmentSetupCommand);
        _companionAppPath = companionAppPath;
        _companionEnvironmentSetupCommand = environmentSetupCommand;
    }

    private async Task StartCompanionAppAsync(string companionAppPath, string? environmentSetupCommand)
    {
        if (!File.Exists(companionAppPath))
            return;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = companionAppPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            if (!string.IsNullOrEmpty(environmentSetupCommand))
            {
                string tempBatchPath = Path.Combine(Path.GetTempPath(), $"llm-router-companion-{Guid.NewGuid():N}.bat");
                var lines = new List<string>
                {
                    "@echo off",
                    environmentSetupCommand,
                    $"call \"{companionAppPath}\"",
                };
                await File.WriteAllLinesAsync(tempBatchPath, lines);
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = "/c \"" + tempBatchPath + "\"";
            }

            _companionProcess = new Process { StartInfo = startInfo };
            _companionProcess.Start();
        }
        catch
        {
            // Don't block server startup if the companion app fails to launch.
            _companionProcess = null;
        }
    }

    private async Task StopCompanionAppAsync()
    {
        if (_companionProcess is not null && !_companionProcess.HasExited)
        {
            try
            {
                _companionProcess.Kill();
                await _companionProcess.WaitForExitAsync();
            }
            catch { /* best effort */ }
        }

        _companionProcess?.Dispose();
        _companionProcess = null;
        _companionAppPath = null;
        _companionEnvironmentSetupCommand = null;
    }

    // --- Backlog + broadcast ---

    private void AddToBacklog(string line)
    {
        lock (_backlogLock)
        {
            _outputBacklog.Add(line);
            if (_outputBacklog.Count > MaxBacklogLines)
                _outputBacklog.RemoveAt(0);
        }
    }

    private List<string> SnapshotBacklog()
    {
        lock (_backlogLock)
            return new List<string>(_outputBacklog);
    }

    private async Task BroadcastAsync(WrapperMessage message)
    {
        var connection = _currentConnection;
        if (connection is null) return;

        try
        {
            await connection.SendAsync(message);
        }
        catch
        {
            // Router disconnected — it'll get the backlog + fresh Hello on its next reconnect.
        }
    }
}
