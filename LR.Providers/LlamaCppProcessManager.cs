using System.Diagnostics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Providers;

/// <summary>
/// Manages the llama.cpp server process lifecycle: startup, health checking,
/// stdout/stderr reading (for timing data), and shutdown.
/// </summary>
public class LlamaCppProcessManager
{
    private readonly ILogger<LlamaCppProcessManager> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LlamaCppTimingCoordinator _timingCoordinator;
    private readonly LlamaCppStdoutParser _stdoutParser;

    /// <summary>
    /// Path to the folder containing the llama.cpp server executable.
    /// </summary>
    public string? ExecutableFolderPath { get; set; }

    /// <summary>
    /// Path to the companion application executable, if any.
    /// </summary>
    public string? CompanionAppPath { get; set; }

    /// <summary>
    /// Shell command to initialize the environment before starting server processes.
    /// </summary>
    public string? EnvironmentSetupCommand { get; set; }

    /// <summary>
    /// The port this instance is listening on.
    /// </summary>
    public int Port { get; set; }

    private Process? _serverProcess;
    private Process? _companionProcess;
    private CancellationTokenSource? _stdoutReaderCts;
    private Task? _stdoutReaderTask;
    private ServerInstance? _serverInstance;

    private const int StartupHealthCheckTimeoutMs = 600_000;
    private const int HealthCheckPollIntervalMs = 2000;
    private const int ProgressReportEverySeconds = 5;

    public LlamaCppProcessManager(
        ILogger<LlamaCppProcessManager> logger,
        IServiceScopeFactory scopeFactory,
        LlamaCppTimingCoordinator timingCoordinator,
        LlamaCppStdoutParser stdoutParser)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _timingCoordinator = timingCoordinator;
        _stdoutParser = stdoutParser;
    }

    /// <summary>
    /// Sets the server instance reference for logging and crash detection purposes.
    /// </summary>
    public void SetServerInstance(ServerInstance? instance)
    {
        _serverInstance = instance;
    }

    /// <summary>
    /// Starts the llama.cpp server process with the given executable path and arguments.
    /// Monitors stdout/stderr for timing data and startup markers.
    /// Returns true if the server started successfully.
    /// </summary>
    public async Task<bool> StartProcessAsync(
        string serverExecutablePath,
        string argString,
        Func<StartupProgressEvent, Task>? onProgress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = serverExecutablePath,
            Arguments = argString,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = ExecutableFolderPath,
        };

        _logger.LogInformation("Starting llama.cpp server at {ServerUrl} with args: {Args}",
            $"http://localhost:{Port}", argString);

        string? tempBatchPath = null;
        bool startupSucceeded = false;
        try
        {
            // Apply environment setup if configured (e.g., oneAPI setvars.bat)
            if (!string.IsNullOrEmpty(EnvironmentSetupCommand))
            {
                tempBatchPath = await CreateTempBatchScriptAsync(serverExecutablePath, startInfo.Arguments ?? string.Empty);
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = "/c \"" + tempBatchPath + "\"";
            }

            // Start companion app first (e.g., SYCL VRAM keeper)
            await StartCompanionAppAsync(cancellationToken);

            // Start the main server process
            _serverProcess = new Process { StartInfo = startInfo };

            // Subscribe to process exit event for immediate crash detection
            _serverProcess.EnableRaisingEvents = true;
            _serverProcess.Exited += async (sender, e) =>
            {
                var exitCode = ((Process)sender!).ExitCode;
                _logger.LogWarning("Server process exited with code {ExitCode}", exitCode);

                // Trigger auto-restart — resolve IAutoRestartService from a scope to avoid
                // injecting a scoped service into this singleton provider
                if (_serverInstance != null)
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var autoRestartService = scope.ServiceProvider.GetService<IAutoRestartService>();
                        if (autoRestartService != null)
                            await autoRestartService.AttemptRestartAsync(_serverInstance);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Auto-restart failed for server {ServerId}", _serverInstance.Id);
                    }
                }
            };

            _serverProcess.Start();

            // Emit progress: process started
            await onProgress?.Invoke(new StartupProgressEvent
            {
                InstanceId = _serverInstance?.Id ?? Guid.Empty,
                EventType = StartupEventType.ProcessStarted,
                Message = "Server process launched, loading model...",
                ElapsedSeconds = 0
            })!;

            // Start long-lived stdout reader for timing data + startup markers
            var outputLines = new System.Collections.Generic.List<string>();
            _stdoutReaderCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Use synchronous ReadLine() to avoid InvalidOperationException from
            // concurrent async operations on the same stream.
            _stdoutReaderTask = Task.Run(() =>
            {
                int lineCount = 0;
                try
                {
                    _logger.LogInformation("[Stats] Stdout reader task started for server on port {Port}", Port);
                    while (!_stdoutReaderCts.Token.IsCancellationRequested)
                    {
                        var line = _serverProcess!.StandardOutput.ReadLine();
                        if (line == null) break;

                        lineCount++;
                        // Collect lines for startup marker detection
                        lock (outputLines) outputLines.Add(line);

                        // Log raw lines that contain "print_timing" to debug parsing issues
                        if (line.Contains("print_timing"))
                        {
                            _logger.LogInformation("[Stats] RAW print_timing line #{LineCount}: {RawLine}",
                                lineCount, line.Substring(0, Math.Min(line.Length, 200)));
                        }

                        // Parse timing events from stdout and feed to coordinator
                        var timingEvent = _stdoutParser.ParseLine(line);
                        if (timingEvent != null)
                        {
                            _logger.LogInformation("[Stats] Parsed: TaskId={TaskId}, Phase={Phase}", timingEvent.TaskId, timingEvent.Phase);
                            _timingCoordinator.ProcessEvent(timingEvent);
                        }
                    }
                    _logger.LogInformation("[Stats] Stdout reader task exited after {LineCount} lines", lineCount);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Stats] Stdout reader task failed after {LineCount} lines", lineCount);
                }
            }, _stdoutReaderCts.Token);

            // Read stderr in a background task — llama.cpp sends print_timing to stderr!
            var stderrTask = Task.Run(() =>
            {
                int stderrLineCount = 0;
                try
                {
                    _logger.LogInformation("[Stats] Stderr reader task started for server on port {Port}", Port);
                    while (!_stdoutReaderCts.Token.IsCancellationRequested)
                    {
                        var line = _serverProcess.StandardError.ReadLine();
                        if (line == null) break;

                        stderrLineCount++;
                        lock (outputLines) outputLines.Add(line);

                        // Log raw lines that contain "print_timing" to debug parsing issues
                        if (line.Contains("print_timing"))
                        {
                            _logger.LogInformation("[Stats] RAW print_timing from STDERR #{LineCount}: {RawLine}",
                                stderrLineCount, line.Substring(0, Math.Min(line.Length, 200)));
                        }

                        // Parse timing events from stderr too (llama.cpp sends them here)
                        var timingEvent = _stdoutParser.ParseLine(line);
                        if (timingEvent != null)
                        {
                            _logger.LogInformation("[Stats] Parsed from STDERR: TaskId={TaskId}, Phase={Phase}", timingEvent.TaskId, timingEvent.Phase);
                            _timingCoordinator.ProcessEvent(timingEvent);
                        }
                    }
                    _logger.LogInformation("[Stats] Stderr reader task exited after {LineCount} lines", stderrLineCount);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Stats] Stderr reader task failed after {LineCount} lines", stderrLineCount);
                }
            }, _stdoutReaderCts.Token);

            // Track startup markers from process output
            bool modelLoadedDetected = false;
            int? detectedPort = null;

            var stopwatch = Stopwatch.StartNew();
            double lastProgressElapsedSeconds = 0;

            while (stopwatch.ElapsedMilliseconds < StartupHealthCheckTimeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_serverProcess.HasExited)
                {
                    string earlyExitSnippet;
                    lock (outputLines)
                        earlyExitSnippet = string.Join("\n", outputLines.TakeLast(10));

                    var msg = $"Server process exited prematurely with code {_serverProcess.ExitCode}. " +
                        (string.IsNullOrEmpty(earlyExitSnippet) ? "" : $"Output: {earlyExitSnippet}");
                    throw new InvalidOperationException(msg);
                }

                // Check output lines for startup markers
                lock (outputLines)
                {
                    foreach (var line in outputLines)
                    {
                        if (!modelLoadedDetected && line.Contains("llama_server: model loaded"))
                        {
                            _logger.LogInformation("Server output: model loaded detected");
                            modelLoadedDetected = true;
                        }

                        // Match "listening on http://127.0.0.1:8081" or "listening on http://localhost:8081"
                        if (!detectedPort.HasValue && line.Contains("llama_server: listening on"))
                        {
                            var listenMatch = System.Text.RegularExpressions.Regex.Match(line, @"listening\s+on\s+http://[^:]+:(\d+)");
                            if (listenMatch.Success)
                            {
                                detectedPort = int.Parse(listenMatch.Groups[1].Value);
                                _logger.LogInformation("Server output: listening on port {DetectedPort}", detectedPort.Value);
                            }
                        }
                    }
                }

                // Primary readiness check: both markers found and port matches
                if (modelLoadedDetected && detectedPort.HasValue)
                {
                    int expectedPort = Port;
                    if (detectedPort.Value == expectedPort || expectedPort <= 0)
                    {
                        double elapsed = stopwatch.ElapsedMilliseconds / 1000.0;
                        _logger.LogInformation("Server started successfully at {ServerUrl} in {ElapsedMs}ms (output markers detected)",
                            $"http://localhost:{Port}", stopwatch.ElapsedMilliseconds);

                        // Emit progress: healthy
                        await onProgress?.Invoke(new StartupProgressEvent
                        {
                            InstanceId = _serverInstance?.Id ?? Guid.Empty,
                            EventType = StartupEventType.Healthy,
                            Message = $"Server is ready on http://localhost:{Port} ({elapsed:F1}s).",
                            ElapsedSeconds = elapsed
                        })!;

                        startupSucceeded = true;
                        return true;
                    }
                }

                await Task.Delay(HealthCheckPollIntervalMs, cancellationToken);

                // Emit progress every ProgressReportEverySeconds to avoid spamming
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

            // Timeout — kill the process and fail with diagnostics
            string outputSnippet;
            lock (outputLines)
                outputSnippet = string.Join("\n", outputLines.TakeLast(10));

            _logger.LogError("Server failed to become healthy within {TimeoutMs}ms. Model loaded: {ModelLoaded}, Detected port: {DetectedPort}. Output: {Output}",
                StartupHealthCheckTimeoutMs, modelLoadedDetected, detectedPort, outputSnippet);

            throw new InvalidOperationException(
                $"Server failed to become healthy within {StartupHealthCheckTimeoutMs / 1000}s. Model loaded={modelLoadedDetected}, Detected port={detectedPort}. " +
                (string.IsNullOrEmpty(outputSnippet) ? "No output captured." : $"Recent output: {outputSnippet}"));
        }
        finally
        {
            // On startup failure, cancel the stdout reader. On success, leave it alive.
            if (!startupSucceeded)
                _stdoutReaderCts?.Cancel();
            CleanupTempBatch(tempBatchPath);
        }
    }

    /// <summary>
    /// Stops all processes (server and companion) and cancels the stdout reader.
    /// </summary>
    public async Task StopAllProcessesAsync(CancellationToken ct)
    {
        await StopServerProcessAsync(ct);
        await StopCompanionAppAsync(ct);
    }

    /// <summary>
    /// Cancels the long-lived stdout/stderr reader tasks.
    /// Called from LlamaCppProvider.Dispose().
    /// </summary>
    public void CancelStdoutReader()
    {
        _stdoutReaderCts?.Cancel();
    }

    /// <summary>
    /// Creates a temporary batch script that initializes the environment then runs the target executable.
    /// Returns the path to the temp file so it can be cleaned up later.
    /// </summary>
    private async Task<string> CreateTempBatchScriptAsync(string executablePath, string arguments)
    {
        string tempBatchPath = Path.Combine(Path.GetTempPath(), $"llm-router-init-{Guid.NewGuid():N}.bat");

        var lines = new List<string>
        {
            "@echo off",
            EnvironmentSetupCommand!,
            $"call \"{executablePath}\" {arguments}",
        };

        await File.WriteAllLinesAsync(tempBatchPath, lines);
        return tempBatchPath;
    }

    /// <summary>
    /// Cleans up temporary batch scripts created during environment initialization.
    /// </summary>
    public void CleanupTempBatch(string? tempPath)
    {
        if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Starts the companion application if one is configured.
    /// </summary>
    private async Task StartCompanionAppAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(CompanionAppPath) || !File.Exists(CompanionAppPath))
            return;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = CompanionAppPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            // If environment setup is configured, wrap the companion app launch in a script
            if (!string.IsNullOrEmpty(EnvironmentSetupCommand))
            {
                string tempBatchPath = Path.Combine(Path.GetTempPath(), $"llm-router-init-{Guid.NewGuid():N}.bat");
                var lines = new List<string>
                {
                    "@echo off",
                    EnvironmentSetupCommand,
                    $"call \"{CompanionAppPath}\"",
                };
                await File.WriteAllLinesAsync(tempBatchPath, lines, ct);
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = "/c \"" + tempBatchPath + "\"";
            }

            _companionProcess = new Process { StartInfo = startInfo };
            _companionProcess.Start();
        }
        catch
        {
            // Log error but don't block server startup if companion app fails
            _companionProcess = null;
        }
    }

    /// <summary>
    /// Stops the companion application if it's running.
    /// </summary>
    private async Task StopCompanionAppAsync(CancellationToken ct)
    {
        if (_companionProcess is not null && !_companionProcess.HasExited)
        {
            try
            {
                _companionProcess.Kill();
                await _companionProcess.WaitForExitAsync(ct);
            }
            catch { /* Ignore errors on companion app shutdown */ }
            finally
            {
                _companionProcess?.Dispose();
                _companionProcess = null;
            }
        }
    }

    /// <summary>
    /// Stops the main server process if it's running.
    /// </summary>
    private async Task StopServerProcessAsync(CancellationToken ct)
    {
        // Cancel the stdout reader before killing the process
        _stdoutReaderCts?.Cancel();

        if (_serverProcess is not null && !_serverProcess.HasExited)
        {
            try
            {
                _serverProcess.Kill();
                await _serverProcess.WaitForExitAsync(ct);
            }
            catch { /* Ignore errors on server shutdown */ }
            finally
            {
                _serverProcess?.Dispose();
                _serverProcess = null;
            }
        }
    }
}
