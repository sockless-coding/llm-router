using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Wrapper;

namespace LR.Providers;

/// <summary>
/// Manages the llama.cpp server process lifecycle via a standalone LR.Wrapper process rather
/// than owning the child process directly. The wrapper survives router restarts; this class is
/// responsible for launching it (or reconnecting to one that's already running), sending it
/// commands over a named pipe, and feeding the raw output it streams back into
/// <see cref="LlamaCppStdoutParser"/>/<see cref="LlamaCppTimingCoordinator"/> exactly as the
/// in-process reader used to.
/// </summary>
public class WrapperProcessManager
{
    private static readonly string StateDirectory =
        WrapperConventions.GetDefaultStateDirectory(AppDomain.CurrentDomain.BaseDirectory);

    private const int StartupHealthCheckTimeoutMs = 600_000;
    private const int HealthCheckPollIntervalMs = 2000;
    private const int ProgressReportEverySeconds = 5;
    private const int WrapperConnectTimeoutMs = 15_000;
    private const int CommandAckTimeoutMs = 15_000;

    private readonly ILogger<WrapperProcessManager> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LlamaCppTimingCoordinator _timingCoordinator;
    private readonly LlamaCppStdoutParser _stdoutParser;

    public string? ExecutableFolderPath { get; set; }
    public string? CompanionAppPath { get; set; }
    public string? EnvironmentSetupCommand { get; set; }
    public int Port { get; set; }

    private ServerInstance? _serverInstance;
    private volatile WrapperPipeConnection? _connection;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private volatile TaskCompletionSource<CommandAckEvent>? _pendingAck;
    private volatile StartupState? _activeStartup;
    private int? _wrapperPid;
    private int? _serverPid;

    /// <summary>Process ID of the wrapper process, if one is currently connected. Diagnostics only.</summary>
    public int? WrapperPid => _wrapperPid;

    /// <summary>Process ID of the managed server process, if one is currently running. Diagnostics only.</summary>
    public int? ServerPid => _serverPid;

    public WrapperProcessManager(
        ILogger<WrapperProcessManager> logger,
        IServiceScopeFactory scopeFactory,
        LlamaCppTimingCoordinator timingCoordinator,
        LlamaCppStdoutParser stdoutParser)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _timingCoordinator = timingCoordinator;
        _stdoutParser = stdoutParser;
    }

    public void SetServerInstance(ServerInstance? instance)
    {
        _serverInstance = instance;
    }

    /// <summary>
    /// Sends the idempotent "ensure running" start command to the wrapper (launching it first if
    /// not already connected) and waits for the same readiness markers the process manager this
    /// replaced used to scan for directly. Used for fresh starts, crash auto-restarts, and
    /// preset-restarts alike — the wrapper never disturbs the companion app across any of them.
    /// </summary>
    public async Task<bool> StartProcessAsync(
        string serverExecutablePath,
        string argString,
        Func<StartupProgressEvent, Task>? onProgress,
        CancellationToken cancellationToken)
    {
        await EnsureWrapperConnectedAsync(cancellationToken);

        var startup = new StartupState();
        _activeStartup = startup;
        try
        {
            var ackTcs = new TaskCompletionSource<CommandAckEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAck = ackTcs;

            _logger.LogInformation("Sending start command to wrapper for {ServerUrl} with args: {Args}",
                $"http://localhost:{Port}", argString);

            await _connection!.SendAsync(new StartServerCommand
            {
                ExecutablePath = serverExecutablePath,
                Arguments = argString,
                WorkingDirectory = ExecutableFolderPath,
                EnvironmentSetupCommand = EnvironmentSetupCommand,
                CompanionAppPath = CompanionAppPath,
                Port = Port,
            }, cancellationToken);

            var ack = await WaitForAckAsync(ackTcs, cancellationToken);
            if (!ack.Success)
                throw new InvalidOperationException($"Wrapper failed to start the server process: {ack.Error}");

            await onProgress?.Invoke(new StartupProgressEvent
            {
                InstanceId = _serverInstance?.Id ?? Guid.Empty,
                EventType = StartupEventType.ProcessStarted,
                Message = "Server process launched, loading model...",
                ElapsedSeconds = 0
            })!;

            var stopwatch = Stopwatch.StartNew();
            double lastProgressElapsedSeconds = 0;

            while (stopwatch.ElapsedMilliseconds < StartupHealthCheckTimeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool exited, modelLoaded;
                int exitCode;
                int? detectedPort;
                string recentOutput;
                lock (startup.Lock)
                {
                    exited = startup.ProcessExited;
                    exitCode = startup.ExitCode;
                    modelLoaded = startup.ModelLoadedDetected;
                    detectedPort = startup.DetectedPort;
                    recentOutput = string.Join("\n", startup.RecentLines);
                }

                if (exited)
                {
                    var msg = $"Server process exited prematurely with code {exitCode}. " +
                        (string.IsNullOrEmpty(recentOutput) ? "" : $"Output: {recentOutput}");
                    throw new InvalidOperationException(msg);
                }

                if (modelLoaded && detectedPort.HasValue && (detectedPort.Value == Port || Port <= 0))
                {
                    double elapsed = stopwatch.ElapsedMilliseconds / 1000.0;
                    _logger.LogInformation("Server started successfully at {ServerUrl} in {ElapsedMs}ms (output markers detected)",
                        $"http://localhost:{Port}", stopwatch.ElapsedMilliseconds);

                    await onProgress?.Invoke(new StartupProgressEvent
                    {
                        InstanceId = _serverInstance?.Id ?? Guid.Empty,
                        EventType = StartupEventType.Healthy,
                        Message = $"Server is ready on http://localhost:{Port} ({elapsed:F1}s).",
                        ElapsedSeconds = elapsed
                    })!;

                    return true;
                }

                await Task.Delay(HealthCheckPollIntervalMs, cancellationToken);

                double currentElapsed = stopwatch.ElapsedMilliseconds / 1000.0;
                if (currentElapsed - lastProgressElapsedSeconds >= ProgressReportEverySeconds)
                {
                    lastProgressElapsedSeconds = currentElapsed;
                    await onProgress?.Invoke(new StartupProgressEvent
                    {
                        InstanceId = _serverInstance?.Id ?? Guid.Empty,
                        EventType = StartupEventType.HealthChecking,
                        Message = $"Waiting for server to be ready... ({currentElapsed:F1}s elapsed)",
                        ElapsedSeconds = currentElapsed
                    })!;
                }
            }

            string outputSnippet;
            lock (startup.Lock)
                outputSnippet = string.Join("\n", startup.RecentLines);

            _logger.LogError("Server failed to become healthy within {TimeoutMs}ms.", StartupHealthCheckTimeoutMs);
            throw new InvalidOperationException(
                $"Server failed to become healthy within {StartupHealthCheckTimeoutMs / 1000}s. " +
                (string.IsNullOrEmpty(outputSnippet) ? "No output captured." : $"Recent output: {outputSnippet}"));
        }
        finally
        {
            _activeStartup = null;
            _pendingAck = null;
        }
    }

    /// <summary>
    /// Stops the main server, the companion app, and tells the wrapper to exit — the full
    /// user-initiated Stop. No-op if nothing is currently connected.
    /// </summary>
    public async Task StopAllProcessesAsync(CancellationToken ct)
    {
        var connection = _connection;
        if (connection is null) return;

        try
        {
            var ackTcs = new TaskCompletionSource<CommandAckEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAck = ackTcs;
            await connection.SendAsync(new StopCommand { StopCompanion = true, ShutdownWrapper = true }, ct);
            await WaitForAckAsync(ackTcs, ct);
        }
        catch
        {
            // Best effort — matches the original process manager's swallow-everything shutdown.
        }
        finally
        {
            _pendingAck = null;
            _wrapperPid = null;
            _serverPid = null;
            await StopPumpAndConnectionAsync();
        }
    }

    /// <summary>
    /// Cancels the background pipe event pump. Called from LlamaCppProvider.Dispose().
    /// </summary>
    public void CancelStdoutReader()
    {
        _pumpCts?.Cancel();
    }

    /// <summary>
    /// Attempts to re-attach to a wrapper process that outlived a previous router process.
    /// Returns true (with monitoring resumed) if a live wrapper with a running server was found;
    /// false otherwise (nothing to reattach to, or the wrapper was idle and has been torn down).
    /// </summary>
    public async Task<bool> TryReconnectAsync(CancellationToken ct)
    {
        if (_serverInstance is null || _connection is not null)
            return false;

        string stateFilePath = WrapperConventions.GetStateFilePath(StateDirectory, _serverInstance.Id);
        if (!File.Exists(stateFilePath))
            return false;

        WrapperStateFile? state;
        try
        {
            var json = await File.ReadAllTextAsync(stateFilePath, ct);
            state = JsonSerializer.Deserialize<WrapperStateFile>(json);
        }
        catch
        {
            state = null;
        }

        if (state is null)
        {
            TryDeleteStateFile(stateFilePath);
            return false;
        }

        if (!IsWrapperProcessAlive(state))
        {
            TryDeleteStateFile(stateFilePath);
            return false;
        }

        NamedPipeClientStream pipeClient;
        try
        {
            pipeClient = new NamedPipeClientStream(".", state.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipeClient.ConnectAsync(WrapperConnectTimeoutMs, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to wrapper pipe for instance {InstanceId} despite a live PID; treating as stale.", _serverInstance.Id);
            TryDeleteStateFile(stateFilePath);
            return false;
        }

        var connection = new WrapperPipeConnection(pipeClient);
        HelloEvent? hello;
        try
        {
            hello = await connection.ReceiveAsync(ct) as HelloEvent;
        }
        catch
        {
            hello = null;
        }

        if (hello is null)
        {
            await connection.DisposeAsync();
            return false;
        }

        if (!hello.ServerRunning)
        {
            // Wrapper alive but nothing running (e.g. the server crashed while the router was
            // down, with nobody around to auto-restart it) — no reason to keep it (and any idle
            // companion) around; the next normal Start spins up a fresh wrapper.
            try { await connection.SendAsync(new StopCommand { StopCompanion = true, ShutdownWrapper = true }, ct); }
            catch { /* best effort */ }
            await connection.DisposeAsync();
            return false;
        }

        _logger.LogInformation("Reconnected to live wrapper for instance {InstanceId} (server pid {ServerPid}).",
            _serverInstance.Id, hello.ServerPid);

        if (hello.Port.HasValue)
            Port = hello.Port.Value;

        _wrapperPid = hello.WrapperPid;
        _serverPid = hello.ServerPid;
        _connection = connection;
        StartPumpTask();
        return true;
    }

    private async Task EnsureWrapperConnectedAsync(CancellationToken ct)
    {
        if (_connection is not null)
            return;

        if (_serverInstance is null)
            throw new InvalidOperationException("Cannot start a wrapper-managed process without a server instance reference.");

        Directory.CreateDirectory(StateDirectory);

        string wrapperExePath = Path.Combine(AppContext.BaseDirectory, WrapperConventions.GetWrapperExecutableName());
        if (!File.Exists(wrapperExePath))
            throw new FileNotFoundException($"Wrapper executable not found at: {wrapperExePath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = wrapperExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--instance-id");
        startInfo.ArgumentList.Add(_serverInstance.Id.ToString());
        startInfo.ArgumentList.Add("--state-dir");
        startInfo.ArgumentList.Add(StateDirectory);

        var wrapperProcess = Process.Start(startInfo);
        _wrapperPid = wrapperProcess?.Id;
        _serverPid = null;

        string pipeName = WrapperConventions.GetPipeName(_serverInstance.Id);
        var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipeClient.ConnectAsync(WrapperConnectTimeoutMs, ct);
        }
        catch (Exception ex)
        {
            pipeClient.Dispose();
            throw new InvalidOperationException("Timed out connecting to the newly launched wrapper process.", ex);
        }

        var connection = new WrapperPipeConnection(pipeClient);
        if (await connection.ReceiveAsync(ct) is not HelloEvent)
        {
            await connection.DisposeAsync();
            throw new InvalidOperationException("Wrapper did not send an initial handshake.");
        }

        _connection = connection;
        StartPumpTask();
    }

    private void StartPumpTask()
    {
        _pumpCts = new CancellationTokenSource();
        _pumpTask = Task.Run(() => PumpEventsAsync(_pumpCts.Token));
    }

    private async Task PumpEventsAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var connection = _connection;
                if (connection is null) break;

                WrapperMessage? message;
                try
                {
                    message = await connection.ReceiveAsync(ct);
                }
                catch
                {
                    break;
                }

                if (message is null) break; // wrapper process gone

                switch (message)
                {
                    case OutputLineEvent oe:
                        HandleOutputLine(oe);
                        break;
                    case ProcessStartedEvent pse:
                        _serverPid = pse.ServerPid;
                        break;
                    case ProcessExitedEvent pe:
                        _serverPid = null;
                        HandleProcessExited(pe);
                        break;
                    case CommandAckEvent ack:
                        _pendingAck?.TrySetResult(ack);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        finally
        {
            _connection = null;
        }
    }

    private void HandleOutputLine(OutputLineEvent oe)
    {
        var timingEvent = _stdoutParser.ParseLine(oe.Line);
        if (timingEvent != null)
            _timingCoordinator.ProcessEvent(timingEvent);

        var startup = _activeStartup;
        if (startup is null) return;

        lock (startup.Lock)
        {
            if (!startup.ModelLoadedDetected && oe.Line.Contains("llama_server: model loaded"))
                startup.ModelLoadedDetected = true;

            if (!startup.DetectedPort.HasValue && oe.Line.Contains("llama_server: listening on"))
            {
                var match = Regex.Match(oe.Line, @"listening\s+on\s+http://[^:]+:(\d+)");
                if (match.Success)
                    startup.DetectedPort = int.Parse(match.Groups[1].Value);
            }

            startup.RecentLines.Add(oe.Line);
            if (startup.RecentLines.Count > 10)
                startup.RecentLines.RemoveAt(0);
        }
    }

    private void HandleProcessExited(ProcessExitedEvent pe)
    {
        var startup = _activeStartup;
        if (startup is not null)
        {
            lock (startup.Lock)
            {
                startup.ProcessExited = true;
                startup.ExitCode = pe.ExitCode;
            }
            return;
        }

        _logger.LogWarning("Server process exited unexpectedly with code {ExitCode}", pe.ExitCode);

        var instance = _serverInstance;
        if (instance is null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var autoRestartService = scope.ServiceProvider.GetService<IAutoRestartService>();
                if (autoRestartService != null)
                    await autoRestartService.AttemptRestartAsync(instance);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-restart failed for server {ServerId}", instance.Id);
            }
        });
    }

    private async Task StopPumpAndConnectionAsync()
    {
        _pumpCts?.Cancel();
        var connection = _connection;
        _connection = null;
        if (connection is not null)
        {
            try { await connection.DisposeAsync(); }
            catch { /* best effort */ }
        }
    }

    private static async Task<CommandAckEvent> WaitForAckAsync(TaskCompletionSource<CommandAckEvent> tcs, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(CommandAckTimeoutMs);
        using var registration = timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token));
        return await tcs.Task;
    }

    private static bool IsWrapperProcessAlive(WrapperStateFile state)
    {
        try
        {
            var proc = Process.GetProcessById(state.WrapperPid);
            if (proc.HasExited) return false;

            var startTimeUtc = proc.StartTime.ToUniversalTime();
            if (Math.Abs((startTimeUtc - state.WrapperStartedAtUtc).TotalSeconds) > 5)
                return false; // PID reuse — a different process now holds this PID

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteStateFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    private sealed class StartupState
    {
        public readonly object Lock = new();
        public bool ModelLoadedDetected;
        public int? DetectedPort;
        public bool ProcessExited;
        public int ExitCode;
        public readonly List<string> RecentLines = new();
    }
}
