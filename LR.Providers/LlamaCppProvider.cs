using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Providers;

/// <summary>
/// Concrete implementation of a llama.cpp-based backend provider.
/// Handles all GPU backends (CUDA, Vulkan, SYCL, CPU) via configuration.
/// </summary>
public class LlamaCppProvider : IBackendProvider, IDisposable
{
    public ServerEngine Engine => ServerEngine.LlamaCpp;

    /// <summary>
    /// Path to the folder containing the llama.cpp server executable (e.g., "llama-server").
    /// Each GPU backend build (CUDA, Vulkan, SYCL) should be in its own folder.
    /// </summary>
    protected string? ExecutableFolderPath { get; set; }

    /// <summary>
    /// Path to the server executable within the folder (e.g., "llama-server.exe" on Windows).
    /// Override for engine-specific defaults.
    /// </summary>
    protected virtual string ServerExecutableName =>
        OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";

    /// <summary>
    /// Full path to the server executable (computed from ExecutableFolderPath + ServerExecutableName).
    /// </summary>
    protected string? ServerExecutablePath
    {
        get
        {
            if (string.IsNullOrEmpty(ExecutableFolderPath)) return null;
            return Path.Combine(ExecutableFolderPath, ServerExecutableName);
        }
    }

    /// <summary>
    /// The port this instance is listening on.
    /// Set by ServerManager during StartProcessAsync via the port parameter.
    /// </summary>
    protected int Port { get; set; }

    /// <summary>
    /// Base URL of the running server (set after StartProcessAsync).
    /// Override or implement in concrete providers.
    /// </summary>
    protected virtual string? ServerUrl => $"http://localhost:{Port}";

    /// <summary>
    /// The GPU backend type this llama.cpp build was compiled for (e.g., CUDA, Vulkan, SYCL).
    /// Can be auto-detected from the folder name or set explicitly.
    /// </summary>
    protected BackendType? GpuBackendType { get; set; }

    /// <summary>
    /// The main server process handle (set after StartProcessAsync).
    /// </summary>
    private Process? _serverProcess;

    /// <summary>
    /// Companion application process (e.g., SYCL VRAM keeper on Windows without display connected).
    /// Set when a companion app is configured and started.
    /// </summary>
    private Process? _companionProcess;

    /// <summary>
    /// Path to the companion application executable, if any.
    /// </summary>
    protected string? CompanionAppPath { get; set; }

    /// <summary>
    /// Shell command to initialize the environment before starting server processes.
    /// For example, "C:\Program Files (x86)\Intel\oneAPI\setvars.bat" intel64 for SYCL backends on Windows.
    /// </summary>
    protected string? EnvironmentSetupCommand { get; set; }

    /// <summary>
    /// HTTP client for communicating with the llama.cpp server.
    /// </summary>
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Logger for this provider instance.
    /// </summary>
    private readonly ILogger<LlamaCppProvider> _logger;

    /// <summary>
    /// Factory for creating scoped service instances (used to resolve IServerLogService safely from a singleton).
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// The server instance this provider is managing (set during startup for logging purposes).
    /// </summary>
    private ServerInstance? _serverInstance;

    /// <summary>
    /// Timeout in milliseconds to wait for the server to become healthy after starting (10 minutes).
    /// Large models can take several minutes to load into GPU memory.
    /// </summary>
    private const int StartupHealthCheckTimeoutMs = 600_000;

    /// <summary>
    /// Interval between health check polls during startup.
    /// </summary>
    private const int HealthCheckPollIntervalMs = 2000;

    /// <summary>
    /// How often to emit a progress event during health checking (every N seconds).
    /// </summary>
    private const int ProgressReportEverySeconds = 5;

    // --- Stdout timing parser and request tracking ---

    /// <summary>
    /// Parser for llama.cpp print_timing stdout lines.
    /// </summary>
    private readonly LlamaCppStdoutParser _stdoutParser;

    /// <summary>
    /// CancellationTokenSource that keeps the long-lived stdout reader alive.
    /// Cancelled when the server is stopped or disposed.
    /// </summary>
    private CancellationTokenSource? _stdoutReaderCts;

    /// <summary>
    /// Background task reading stdout lines and parsing timing events (lives for the duration of the server process).
    /// </summary>
    private Task? _stdoutReaderTask;

    /// <summary>
    /// Pending requests waiting to be assigned a task_id from stdout.
    /// Each entry holds the RouteResponse being built and the enqueue time.
    /// </summary>
    private readonly ConcurrentQueue<(DateTimeOffset EnqueueTime, RouteResponse Response)> _pendingRequests = new();

    /// <summary>
    /// Active requests mapped by llama.cpp task_id to their RouteResponse.
    /// Used to merge timing data into the correct response when completion lines appear.
    /// </summary>
    private readonly ConcurrentDictionary<int, (RouteResponse Response, DateTimeOffset StartTime)> _activeRequests = new();

    /// <summary>
    /// Accumulated timing data per task_id from stdout parsing.
    /// Updated incrementally as print_timing lines arrive.
    /// </summary>
    private readonly ConcurrentDictionary<int, LlamaCppTaskTiming> _taskTimings = new();

    public LlamaCppProvider(
        ILogger<LlamaCppProvider> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _stdoutParser = new LlamaCppStdoutParser();
    }

    /// <summary>
    /// Sets the server instance reference for logging purposes.
    /// </summary>
    public void SetServerInstance(ServerInstance? instance)
    {
        _serverInstance = instance;
    }

    /// <summary>
    /// Applies engine-specific configuration from the backend config data.
    /// Override to add provider-specific configuration handling.
    /// </summary>
    public virtual void Configure(BackendConfigData configData)
    {
        ExecutableFolderPath = configData.LlamaCppExecutableFolderPath;
        CompanionAppPath = configData.CompanionAppPath;
        EnvironmentSetupCommand = configData.EnvironmentSetupCommand;
    }

    public virtual void StartPort(int? port)
    {
        if (port.HasValue) Port = port.Value;
    }

    public async Task<bool> StartProcessAsync(ModelPreset preset, int? port = null, Func<StartupProgressEvent, Task>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(ServerExecutablePath))
            throw new InvalidOperationException($"Server executable path is not set. ExecutableFolderPath: '{ExecutableFolderPath}'");

        if (!File.Exists(ServerExecutablePath))
            throw new FileNotFoundException($"Server executable not found at: {ServerExecutablePath}");

        // Update port if provided
        if (port.HasValue) Port = port.Value;

        // Auto-detect GPU backend type from folder name
        GpuBackendType = DetectGpuBackendType(ExecutableFolderPath);

        var args = BuildArgs(preset);
        string argString = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

        var startInfo = new ProcessStartInfo
        {
            FileName = ServerExecutablePath,
            Arguments = argString,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = ExecutableFolderPath,
        };

        _logger.LogInformation("Starting llama.cpp server at {ServerUrl} with args: {Args}", ServerUrl, argString);
        await LogProviderMessage(ServerLogLevel.Info,
            $"Starting server on port {Port}. Args: {argString.Substring(0, Math.Min(argString.Length, 200))}{(argString.Length > 200 ? "..." : "")}");

        // Apply environment setup if configured (e.g., oneAPI setvars.bat)
        string? tempBatchPath = null;
        bool startupSucceeded = false;
        try
        {
            // InitializeEnvironmentAsync may create a temp batch file — track it for cleanup
            if (!string.IsNullOrEmpty(EnvironmentSetupCommand))
            {
                tempBatchPath = await CreateTempBatchScriptAsync(ServerExecutablePath, startInfo.Arguments ?? string.Empty);
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
                await LogProviderMessage(ServerLogLevel.Warning,
                    $"Server process exited unexpectedly with code {exitCode}.");

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

                        // Parse timing events from stdout
                        var timingEvent = _stdoutParser.ParseLine(line);
                        if (timingEvent != null)
                        {
                            _logger.LogInformation("[Stats] Parsed: TaskId={TaskId}, Phase={Phase}", timingEvent.TaskId, timingEvent.Phase);
                            ProcessTimingEvent(timingEvent, outputLines);
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
            // Use synchronous ReadLine() to avoid InvalidOperationException from
            // concurrent async operations on the same stream.
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
                            ProcessTimingEvent(timingEvent, outputLines);
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

            // Poll until server is ready (via output markers) or timeout
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
                        _logger.LogInformation("Server started successfully at {ServerUrl} in {ElapsedMs}ms (output markers detected)", ServerUrl, stopwatch.ElapsedMilliseconds);
                        await LogProviderMessage(ServerLogLevel.Info,
                            $"Server started successfully on {ServerUrl} ({stopwatch.ElapsedMilliseconds}ms).");

                        // Emit progress: healthy
                        await onProgress?.Invoke(new StartupProgressEvent
                        {
                            InstanceId = _serverInstance?.Id ?? Guid.Empty,
                            EventType = StartupEventType.Healthy,
                            Message = $"Server is ready on {ServerUrl} ({elapsed:F1}s).",
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
            await LogProviderMessage(ServerLogLevel.Error,
                $"Server failed to start within {StartupHealthCheckTimeoutMs / 1000}s. Model loaded detected={modelLoadedDetected}, Detected port={detectedPort}.{(string.IsNullOrEmpty(outputSnippet) ? " No output captured." : $" Recent output:\n{outputSnippet}")}");

            throw new InvalidOperationException(
                $"Server failed to become healthy within {StartupHealthCheckTimeoutMs / 1000}s. Model loaded={modelLoadedDetected}, Detected port={detectedPort}. " +
                (string.IsNullOrEmpty(outputSnippet) ? "No output captured." : $"Recent output: {outputSnippet}"));
        }
        finally
        {
            // On startup failure, cancel the stdout reader. On success, leave it alive.
            if (!startupSucceeded)
                _stdoutReaderCts?.Cancel();
            // Clean up temp batch file if we created one
            CleanupTempBatch(tempBatchPath);
        }
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

    public async Task StopProcessAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Stopping server at {ServerUrl}", ServerUrl);
        await LogProviderMessage(ServerLogLevel.Info, "Server stop initiated.");

        if (_serverProcess?.HasExited == true)
        {
            var exitCode = _serverProcess.ExitCode;
            _logger.LogWarning("Server process already exited with code {ExitCode}", exitCode);
            await LogProviderMessage(ServerLogLevel.Warning,
                $"Server process had already exited with code {exitCode}.");
            return;
        }

        await StopServerProcessAsync(cancellationToken);
        await StopCompanionAppAsync(cancellationToken);
    }

    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(ServerUrl)) return false;

                // Use a short-lived HttpClient for the health check instead of modifying
                // the shared client's timeout, which would affect subsequent requests.
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var response = await httpClient.GetAsync($"{ServerUrl}/health", cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

    public async Task<RouteResponse?> SendRequestAsync(string payload, CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrEmpty(ServerUrl)) return null;

                // Register this request for timing data collection from stdout
                var routeResponse = new RouteResponse();
                _pendingRequests.Enqueue((DateTimeOffset.UtcNow, routeResponse));
                _logger.LogInformation("[Stats] Enqueued non-streaming request. Pending={_PendingSize}", _pendingRequests.Count);

                var response = await _httpClient.PostAsJsonAsync(
                    $"{ServerUrl}/v1/chat/completions",
                    JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payload),
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new HttpRequestException($"Chat completion failed: {response.StatusCode} - {errorBody}");
                }

                var jsonDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                ParseRouteResponseInto(jsonDoc.RootElement, routeResponse);

            // Non-streaming request: by the time we get here, stdout parsing should have
            // already captured completion timing. Merge it into our response.
            _logger.LogInformation("[Stats] Before merge - PromptMs={PromptMs}, GenMs={GenMs}, TotalMs={TotalMs}",
                routeResponse.PromptProcessingMs, routeResponse.GenerationMs, routeResponse.TotalLatencyMs);
            MergeTimingData(routeResponse);
            _logger.LogInformation("[Stats] After merge - PromptMs={PromptMs:F0}, GenMs={GenMs:F0}, TotalMs={TotalMs:F0}, TokensProcessed={Tokens}",
                routeResponse.PromptProcessingMs, routeResponse.GenerationMs, routeResponse.TotalLatencyMs, routeResponse.PromptTokensProcessed);

            return routeResponse;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ServerUrl != null)
        {
            _logger.LogError(ex, "Request failed to {ServerUrl}", ServerUrl);
            await LogProviderMessage(ServerLogLevel.Error,
                $"Request failed: {ex.Message}");

            throw;
        }
    }

    /// <summary>
    /// Logs a message to both the console (via ILogger) and the database (via IServerLogService).
    /// Uses IServiceScopeFactory to resolve IServerLogService in a new scope, avoiding
    /// the captured-dependency anti-pattern of injecting scoped services into singletons.
    /// </summary>
    private async Task LogProviderMessage(ServerLogLevel level, string message)
    {
        // Log to console via ILogger (always works)
        var logLevel = level switch
        {
            ServerLogLevel.Info => Microsoft.Extensions.Logging.LogLevel.Information,
            ServerLogLevel.Warning => Microsoft.Extensions.Logging.LogLevel.Warning,
            ServerLogLevel.Error => Microsoft.Extensions.Logging.LogLevel.Error,
            _ => Microsoft.Extensions.Logging.LogLevel.Information,
        };

        if (_serverInstance != null)
        {
            _logger.Log(logLevel, "[{ServerId}] {Message}", _serverInstance.Id, message);
        }
        else
        {
            _logger.Log(logLevel, message);
        }

        // Persist to database via scoped resolution of IServerLogService
        if (_serverInstance != null)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var logService = scope.ServiceProvider.GetRequiredService<IServerLogService>();
                await logService.LogAsync(_serverInstance, level, message);
            }
            catch (Exception ex)
            {
                // Don't let logging failures break provider operations
                _logger.LogError(ex, "Failed to persist log message to database for server {ServerId}", _serverInstance?.Id);
            }
        }
    }

    /// <summary>
    /// Sends a streaming inference request. Returns token chunks as they are generated.
    /// </summary>
    public async IAsyncEnumerable<RouteStreamChunk> SendStreamRequestAsync(
        string payload, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(ServerUrl)) yield break;

        Stream? stream = null;
        StreamReader? reader = null;

        // Register this request for timing data collection from stdout
        var streamResponse = new RouteResponse();
        _pendingRequests.Enqueue((DateTimeOffset.UtcNow, streamResponse));
        _logger.LogInformation("[Stats] Enqueued streaming request. Pending={_PendingSize}", _pendingRequests.Count);

        try
        {
            var requestContent = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(
                $"{ServerUrl}/v1/chat/completions?stream=true",
                requestContent,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"Streaming completion failed: {response.StatusCode} - {errorBody}");
            }

            stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            reader = new StreamReader(stream);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ServerUrl != null)
        {
            _logger.LogError(ex, "Streaming request failed to {ServerUrl}", ServerUrl);
            await LogProviderMessage(ServerLogLevel.Error,
                $"Streaming request failed: {ex.Message}");

            throw new InvalidOperationException($"Failed to send streaming request to {ServerUrl}: {ex.Message}", ex);
        }

        string? accumulatedText = null;
        bool completed = false;

        while (!completed && !cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync();
            if (line is null || string.IsNullOrWhiteSpace(line)) continue;

            // llama.cpp SSE format: "data: {json}"
            if (!line.StartsWith("data:")) continue;

            string data = line.Substring(5).Trim();
            if (string.IsNullOrEmpty(data) || data == "[DONE]") continue;

            RouteStreamChunk? chunk = null;
            try
            {
                using var jsonDoc = JsonDocument.Parse(data);
                var root = jsonDoc.RootElement;

                // Extract text delta from choices[0].delta.content
                string? textDelta = null;
                if (root.TryGetProperty("choices", out JsonElement choices) && choices.GetArrayLength() > 0)
                {
                    var firstChoice = choices[0];
                    if (firstChoice.TryGetProperty("delta", out JsonElement delta))
                    {
                        textDelta = delta.TryGetProperty("content", out JsonElement content)
                            ? content.GetString()
                            : null;
                    }

                    // Check for finish_reason to detect end of stream
                    string? finishReason = firstChoice.TryGetProperty("finish_reason", out JsonElement fr)
                        ? fr.GetString()
                        : null;

                    if (!string.IsNullOrEmpty(textDelta))
                    {
                        accumulatedText += textDelta;
                        chunk = new RouteStreamChunk { TextDelta = textDelta, IsFinal = false };
                    }

                    // If finish_reason is set and we have usage data, send final chunk
                    if (finishReason != null)
                    {
                        BuildRouteResponseFromStreamInto(accumulatedText ?? string.Empty, root, streamResponse);
                        _logger.LogInformation("[Stats] Stream complete - Before merge PromptMs={PromptMs}, GenMs={GenMs}",
                            streamResponse.PromptProcessingMs, streamResponse.GenerationMs);
                        // Merge timing data from stdout parsing
                        MergeTimingData(streamResponse);
                        _logger.LogInformation("[Stats] Stream complete - After merge PromptMs={PromptMs}, GenMs={GenMs}, TotalMs={TotalMs}",
                            streamResponse.PromptProcessingMs, streamResponse.GenerationMs, streamResponse.TotalLatencyMs);
                        chunk = new RouteStreamChunk { IsFinal = true, Response = streamResponse };
                        completed = true;
                    }
                }
            }
            catch (JsonException)
            {
                // Skip malformed SSE data lines
                continue;
            }

            if (chunk != null)
                yield return chunk;
        }

        // If stream ended without a proper finish_reason, send final chunk with what we have
        if (!completed && !string.IsNullOrEmpty(accumulatedText))
        {
            streamResponse.Payload = accumulatedText ?? string.Empty;
            MergeTimingData(streamResponse);
            yield return new RouteStreamChunk { IsFinal = true, Response = streamResponse };
        }
    }

    /// <summary>
    /// Builds the command-line arguments from a ModelPreset.
    /// Override to add backend-specific flags.
    /// </summary>
    protected virtual List<string> BuildArgs(ModelPreset preset)
    {
        var args = new List<string>();

        // --- Core settings ---
        args.Add("--model");
        args.Add(preset.ModelPath);
        AddIntArg(args, "--ctx-size", preset.ContextSize);
        AddIntArg(args, "--gpu-layers", preset.GpuLayers);
        AddArgIfSet(args, "--cache-type-k", preset.CacheTypeK);
        AddArgIfSet(args, "--cache-type-v", preset.CacheTypeV);
        AddArgIfSet(args, "--flash-attn", preset.FlashAttention);
        if (preset.Jinja.HasValue)
            args.Add(preset.Jinja.Value ? "--jinja" : "--no-jinja");
        AddArgIfSet(args, "--spec-type", preset.SpecType);

        // --- Sampling (core) ---
        AddFloatArg(args, "--temp", preset.Temperature);
        AddIntArg(args, "--top-k", preset.TopK);
        AddFloatArg(args, "--min-p", preset.MinP);
        AddFloatArg(args, "--top-p", preset.TopP);
        AddFloatArg(args, "--repeat-penalty", preset.RepeatPenalty);
        AddFloatArg(args, "--presence-penalty", preset.PresencePenalty);

        // --- Advanced: Generation ---
        AddIntArg(args, "--threads", preset.Threads);
        AddIntArg(args, "--threads-batch", preset.ThreadsBatch);
        AddIntArg(args, "--n-predict", preset.PredictN);
        AddIntArg(args, "--batch-size", preset.BatchSize);
        AddIntArg(args, "--ubatch-size", preset.UbatchSize);
        AddIntArg(args, "--keep", preset.KeepN);
        AddLongArg(args, "--seed", preset.Seed);
        if (preset.IgnoreEos.HasValue && preset.IgnoreEos.Value)
            args.Add("--ignore-eos");

        // --- Advanced: GPU/Device ---
        AddArgIfSet(args, "--device", preset.Device);
        AddArgIfSet(args, "--split-mode", preset.SplitMode);
        AddArgIfSet(args, "--tensor-split", preset.TensorSplit);
        AddIntArg(args, "--main-gpu", preset.MainGpu);
        AddArgIfSet(args, "--fit", preset.Fit);
        if (preset.KvOffload.HasValue)
            args.Add(preset.KvOffload.Value ? "--kv-offload" : "--no-kv-offload");
        if (preset.Repack.HasValue)
            args.Add(preset.Repack.Value ? "--repack" : "--no-repack");

        // --- Advanced: Memory ---
        AddArgIfSet(args, "--load-mode", preset.LoadMode);
        AddIntArg(args, "--cache-ram", preset.CacheRam);

        // --- Advanced: RoPE Scaling ---
        AddArgIfSet(args, "--rope-scaling", preset.RopeScalingType);
        AddFloatArg(args, "--rope-scale", preset.RopeScale);
        AddFloatArg(args, "--rope-freq-base", preset.RopeFreqBase);
        AddFloatArg(args, "--rope-freq-scale", preset.RopeFreqScale);
        AddIntArg(args, "--yarn-orig-ctx", preset.YarnOrigCtx);
        AddFloatArg(args, "--yarn-ext-factor", preset.YarnExtFactor);
        AddFloatArg(args, "--yarn-attn-factor", preset.YarnAttnFactor);
        AddFloatArg(args, "--yarn-beta-slow", preset.YarnBetaSlow);
        AddFloatArg(args, "--yarn-beta-fast", preset.YarnBetaFast);

        // --- Advanced: Sampling (Extended) ---
        AddFloatArg(args, "--top-n-sigma", preset.TopNSigma);
        AddFloatArg(args, "--xtc-probability", preset.XtcProbability);
        AddFloatArg(args, "--xtc-threshold", preset.XtcThreshold);
        AddFloatArg(args, "--typical-p", preset.TypicalP);
        AddIntArg(args, "--repeat-last-n", preset.RepeatLastN);
        AddFloatArg(args, "--frequency-penalty", preset.FrequencyPenalty);
        AddFloatArg(args, "--dry-multiplier", preset.DryMultiplier);
        AddFloatArg(args, "--dry-base", preset.DryBase);
        AddIntArg(args, "--dry-allowed-length", preset.DryAllowedLength);
        AddIntArg(args, "--dry-penalty-last-n", preset.DryPenaltyLastN);
        AddIntArg(args, "--mirostat", preset.Mirostat);
        AddFloatArg(args, "--mirostat-lr", preset.MirostatTau);
        AddFloatArg(args, "--mirostat-ent", preset.MirostatEta);
        AddFloatArg(args, "--dynatemp-range", preset.DynatempRange);
        AddFloatArg(args, "--dynatemp-exp", preset.DynatempExp);

        // --- Advanced: Speculative Decoding ---
        AddArgIfSet(args, "--spec-draft-model", preset.SpecDraftModel);
        AddIntArg(args, "--spec-draft-n-max", preset.SpecDraftNMax);
        AddIntArg(args, "--spec-draft-n-min", preset.SpecDraftNMin);
        AddFloatArg(args, "--draft-p-min", preset.SpecDraftPMin);
        AddArgIfSet(args, "--cache-type-k-draft", preset.SpecDraftTypeK);
        AddArgIfSet(args, "--cache-type-v-draft", preset.SpecDraftTypeV);
        AddIntArg(args, "--gpu-layers-draft", preset.SpecDraftGpuLayers);
        AddIntArg(args, "--spec-draft-threads", preset.SpecDraftThreads);

        // --- Advanced: Server ---
        AddArgIfSet(args, "--host", preset.Host);
        AddIntArg(args, "--port", preset.Port ?? Port);
        AddIntArg(args, "--parallel", preset.Parallel);
        if (preset.ContBatching.HasValue)
            args.Add(preset.ContBatching.Value ? "--cont-batching" : "--no-cont-batching");
        AddIntArg(args, "--timeout", preset.Timeout);
        AddArgIfSet(args, "--api-key", preset.ApiKey);
        if (preset.CachePrompt.HasValue)
            args.Add(preset.CachePrompt.Value ? "--cache-prompt" : "--no-cache-prompt");

        // --- Advanced: Reasoning ---
        AddArgIfSet(args, "--reasoning", preset.Reasoning);
        AddIntArg(args, "--reasoning-budget", preset.ReasoningBudget);

        // --- Advanced: Multimodal ---
        AddArgIfSet(args, "--mmproj", preset.Mmproj);
        AddIntArg(args, "--image-min-tokens", preset.ImageMinTokens);
        AddIntArg(args, "--image-max-tokens", preset.ImageMaxTokens);

        // --- Advanced: LoRA ---
        AddArgIfSet(args, "--lora", preset.Lora);

        // --- Advanced: Chat Template ---
        AddArgIfSet(args, "--chat-template", preset.ChatTemplate);

        // --- Fallback flags (from Flags dictionary) ---
        foreach (var flag in preset.Flags)
            args.Add(flag.Key);

        return args;
    }

    public virtual string? GetStartCommand(ModelPreset preset, int? port = null)
    {
        if (string.IsNullOrEmpty(ServerExecutablePath) || !File.Exists(ServerExecutablePath))
            return null;

        var args = BuildArgs(preset);
        string argString = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

        // If environment setup is configured, the actual command goes through cmd.exe + batch script
        if (!string.IsNullOrEmpty(EnvironmentSetupCommand))
            return $"cmd.exe /c \"call \"\"{EnvironmentSetupCommand}\"\" && call \"\"{ServerExecutablePath}\"\" {argString}\"";

        return $"\"{ServerExecutablePath}\" {argString}";
    }

    private static void AddArgIfSet(List<string> args, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            args.Add(name);
            args.Add(value);
        }
    }

    private static void AddIntArg(List<string> args, string name, int? value)
    {
        if (value.HasValue)
        {
            args.Add(name);
            args.Add(value.Value.ToString());
        }
    }

    private static void AddLongArg(List<string> args, string name, long? value)
    {
        if (value.HasValue)
        {
            args.Add(name);
            args.Add(value.Value.ToString());
        }
    }

    private static void AddFloatArg(List<string> args, string name, float? value)
    {
        if (value.HasValue)
        {
            args.Add(name);
            args.Add(value.Value.ToString("G7"));
        }
    }

    /// <summary>
    /// Auto-detects the GPU backend type from the executable folder path name.
    /// </summary>
    private static BackendType DetectGpuBackendType(string? folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return BackendType.Unknown;

        string lower = folderPath.ToLowerInvariant();
        if (lower.Contains("cuda")) return BackendType.Cuda;
        if (lower.Contains("vulkan")) return BackendType.Vulkan;
        if (lower.Contains("sycl") || lower.Contains("oneapi")) return BackendType.Sycl;

        return BackendType.Unknown;
    }

    /// <summary>
    /// Parses a non-streaming llama.cpp OpenAI-compatible response into a new RouteResponse.
    /// </summary>
    private static RouteResponse ParseRouteResponse(JsonElement root)
    {
        var response = new RouteResponse();
        ParseRouteResponseInto(root, response);
        return response;
    }

    /// <summary>
    /// Parses llama.cpp OpenAI-compatible JSON response into an existing RouteResponse.
    /// Used when we pre-allocate the RouteResponse for stdout timing correlation.
    /// </summary>
    private static void ParseRouteResponseInto(JsonElement root, RouteResponse response)
    {
        // Extract content from choices[0].message.content
        if (root.TryGetProperty("choices", out JsonElement choices) && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("message", out JsonElement message))
            {
                response.Payload = message.TryGetProperty("content", out JsonElement content)
                    ? content.GetString() ?? string.Empty
                    : string.Empty;
            }
        }

        // Extract usage data
        if (root.TryGetProperty("usage", out JsonElement usage))
        {
            response.PromptTokensProcessed = GetInt32(usage, "prompt_tokens") ?? 0;
            response.GeneratedTokenCount = GetInt32(usage, "completion_tokens") ?? 0;
        }

        // Extract timing data (llama.cpp may include these in the top-level or under usage)
        if (root.TryGetProperty("timing", out JsonElement timing))
        {
            response.PromptProcessingMs = GetDouble(timing, "prompt_ms") ?? 0;
            response.GenerationMs = GetDouble(timing, "predicted_ms") ?? 0;
        }

        // Some llama.cpp versions put timings in usage
        if (root.TryGetProperty("usage", out JsonElement usageTiming))
        {
            if (response.PromptProcessingMs == 0)
                response.PromptProcessingMs = GetDouble(usageTiming, "prompt_ms") ?? 0;
            if (response.GenerationMs == 0)
                response.GenerationMs = GetDouble(usageTiming, "predicted_ms") ?? GetDouble(usageTiming, "time_generation_ms") ?? 0;
        }

        // First token latency from timing if available
        if (root.TryGetProperty("timing", out JsonElement timingFirst))
        {
            response.FirstTokenLatencyMs = GetDouble(timingFirst, "predicted_n_first_token_ms") ?? 0;
        }

        response.TotalLatencyMs = response.PromptProcessingMs + response.GenerationMs;
    }

    /// <summary>
    /// Builds a RouteResponse from streaming metadata.
    /// </summary>
    private static RouteResponse BuildRouteResponseFromStream(string accumulatedText, JsonElement root)
    {
        var response = new RouteResponse { Payload = accumulatedText };
        BuildRouteResponseFromStreamInto(accumulatedText, root, response);
        return response;
    }

    /// <summary>
    /// Populates an existing RouteResponse from streaming metadata.
    /// Used when we pre-allocate the RouteResponse for stdout timing correlation.
    /// </summary>
    private static void BuildRouteResponseFromStreamInto(string accumulatedText, JsonElement root, RouteResponse response)
    {
        response.Payload = accumulatedText;

        if (root.TryGetProperty("usage", out JsonElement usage))
        {
            response.PromptTokensProcessed = GetInt32(usage, "prompt_tokens") ?? 0;
            response.GeneratedTokenCount = GetInt32(usage, "completion_tokens") ?? 0;
        }
    }

    private static int? GetInt32(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Number)
            return value.GetInt32();
        return null;
    }

    private static double? GetDouble(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Number)
            return value.GetDouble();
        return null;
    }

    /// <summary>
    /// Disposes the HTTP client and cancels any running stdout reader.
    /// </summary>
    public void Dispose()
    {
        // Cancel the long-lived stdout reader
        _stdoutReaderCts?.Cancel();
        _httpClient.Dispose();
    }

    /// <summary>
    /// Initializes the environment by running the configured setup command.
    /// For example, runs "C:\Program Files (x86)\Intel\oneAPI\setvars.bat" intel64 for SYCL backends.
    /// 
    /// This method creates a temporary batch script that first runs the setup command,
    /// then launches the target executable. This ensures environment variables set by
    /// the init script (like oneAPI's setvars.bat) are inherited by subsequent processes.
    /// </summary>
    protected virtual async Task InitializeEnvironmentAsync(string executablePath, ProcessStartInfo startInfo, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(EnvironmentSetupCommand))
            return;

        // Build a temporary batch script that initializes the environment then runs the target executable.
        // This ensures env vars set by scripts like oneAPI's setvars.bat are inherited.
        string tempBatchPath = Path.Combine(Path.GetTempPath(), $"llm-router-init-{Guid.NewGuid():N}.bat");

        try
        {
            var lines = new List<string>
            {
                "@echo off",
                // Run the environment setup command (e.g., setvars.bat intel64)
                EnvironmentSetupCommand,
                // Launch the target executable and pass through its exit code
                $"call \"{executablePath}\" {startInfo.Arguments ?? string.Empty}",
            };

            await File.WriteAllLinesAsync(tempBatchPath, lines, ct);

            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = "/c \"" + tempBatchPath + "\"";
            // When using cmd.exe /c with a batch file, UseShellExecute must be false for redirection to work.
            startInfo.UseShellExecute = false;
        }
        catch
        {
            // If we can't create the temp script, fall through without environment setup.
            // The process will still launch, just without initialized env vars.
        }
    }

    /// <summary>
    /// Cleans up temporary batch scripts created by InitializeEnvironmentAsync.
    /// </summary>
    protected void CleanupTempBatch(string? tempPath)
    {
        if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Starts the companion application if one is configured.
    /// Override for backend-specific companion app behavior (e.g., SYCL VRAM keeper on Windows).
    /// </summary>
    protected virtual async Task StartCompanionAppAsync(CancellationToken ct = default)
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
            // that first initializes the environment (e.g., oneAPI setvars.bat)
            await InitializeEnvironmentAsync(CompanionAppPath, startInfo, ct);

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
    protected virtual async Task StopCompanionAppAsync(CancellationToken ct = default)
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
    protected virtual async Task StopServerProcessAsync(CancellationToken ct = default)
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

    // --- Stdout timing event processing and request tracking ---

    /// <summary>
    /// Processes a parsed timing event from stdout.
    /// Assigns new task_ids to pending requests (FIFO) and merges completion data into active responses.
    /// </summary>
    private void ProcessTimingEvent(LlamaCppTimingEvent evt, System.Collections.Generic.List<string> outputLines)
    {
        // Get or create the accumulated timing for this task
        var isNew = !_taskTimings.ContainsKey(evt.TaskId);
        var timing = _taskTimings.GetOrAdd(evt.TaskId, _ => new LlamaCppTaskTiming { TaskId = evt.TaskId });

        if (isNew)
            _logger.LogInformation("[Stats] New stdout task detected: TaskId={TaskId}, Phase={Phase}", evt.TaskId, evt.Phase);

        switch (evt.Phase)
        {
            case LlamaCppTimingPhase.PromptProcessing:
                // Update progress info — also this is the first sign a task_id appeared,
                // so assign it to a pending request if we have one.
                timing.PromptProgress = evt.Progress ?? 0;
                AssignTaskToPendingRequest(evt.TaskId);
                break;

            case LlamaCppTimingPhase.Generation:
                timing.NDecoded = evt.NDecoded;
                // Generation phase also means the task is active — assign if not yet assigned.
                AssignTaskToPendingRequest(evt.TaskId);
                break;

            case LlamaCppTimingPhase.Completion:
                // Merge completion summary data into accumulated timing
                ApplyCompletionEvent(timing, evt);
                _logger.LogInformation("[Stats] Completion event for TaskId={TaskId}: PromptEvalMs={PromptEvalMs}, EvalMs={EvalMs}, TotalMs={TotalMs}",
                    evt.TaskId, evt.PromptEvalMs, evt.EvalMs, evt.TotalMs);
                // Try to merge into the associated RouteResponse
                MergeTimingIntoActiveRequest(evt.TaskId, timing);
                break;
        }
    }

    /// <summary>
    /// Assigns a newly seen task_id to the oldest pending request (FIFO).
    /// </summary>
    private void AssignTaskToPendingRequest(int taskId)
    {
        // Check if already assigned
        if (_activeRequests.ContainsKey(taskId))
            return;

        // Try to dequeue a pending request
        while (_pendingRequests.TryDequeue(out var pending))
        {
            _activeRequests[taskId] = (pending.Response, pending.EnqueueTime);
            _logger.LogInformation("[Stats] Assigned task {TaskId} to request. Active={_ActiveCount}, Pending={_PendingCount}",
                taskId, _activeRequests.Count, _pendingRequests.Count);
            return;
        }

        // If we get here, no pending request was found for this task_id
        if (!_activeRequests.ContainsKey(taskId))
            _logger.LogWarning("[Stats] No pending request found for stdout task {TaskId}! Pending queue empty. This means a task appeared before any request was enqueued.", taskId);
    }

    /// <summary>
    /// Applies a completion summary event's data into the accumulated LlamaCppTaskTiming.
    /// </summary>
    private static void ApplyCompletionEvent(LlamaCppTaskTiming timing, LlamaCppTimingEvent evt)
    {
        if (evt.PromptEvalMs.HasValue) {
            timing.PromptEvalMs = evt.PromptEvalMs;
            timing.PromptTokens = evt.PromptTokens;
            timing.PromptTokensPerSec = evt.PromptTokensPerSec;
        }

        if (evt.EvalMs.HasValue) {
            timing.EvalMs = evt.EvalMs;
            timing.GeneratedTokens = evt.GeneratedTokens;
            timing.GenTokensPerSec = evt.GenTokensPerSecCompletion;
        }

        if (evt.TotalMs.HasValue)
            timing.TotalMs = evt.TotalMs;

        if (evt.DraftAcceptanceRate.HasValue) {
            timing.DraftAcceptanceRate = evt.DraftAcceptanceRate;
            timing.DraftAccepted = evt.DraftAccepted;
            timing.DraftGenerated = evt.DraftGenerated;
            timing.DraftMeanLen = evt.DraftMeanLen;
        }
    }

    /// <summary>
    /// Merges accumulated stdout timing data into the RouteResponse for an active request.
    /// </summary>
    private void MergeTimingIntoActiveRequest(int taskId, LlamaCppTaskTiming timing)
    {
        if (!_activeRequests.TryGetValue(taskId, out var entry))
            return;

        var response = entry.Response;

        // Only overwrite timing values from stdout if they're non-zero (stdout data is authoritative here).
        if (timing.PromptEvalMs.HasValue && timing.PromptEvalMs.Value > 0)
            response.PromptProcessingMs = timing.PromptEvalMs.Value;

        if (timing.EvalMs.HasValue && timing.EvalMs.Value > 0)
            response.GenerationMs = timing.EvalMs.Value;

        // Total latency from stdout is the most accurate.
        if (timing.TotalMs.HasValue && timing.TotalMs.Value > 0)
            response.TotalLatencyMs = timing.TotalMs.Value;
        else if (response.PromptProcessingMs > 0 || response.GenerationMs > 0)
            response.TotalLatencyMs = response.PromptProcessingMs + response.GenerationMs;

        // Speculative decoding metrics (only populated when speculative decoding is active)
        if (timing.DraftAcceptanceRate.HasValue && timing.DraftAcceptanceRate.Value > 0)
        {
            response.DraftAcceptanceRate = timing.DraftAcceptanceRate;
            response.DraftAccepted = timing.DraftAccepted;
            response.DraftGenerated = timing.DraftGenerated;
            response.DraftMeanLen = timing.DraftMeanLen;
        }

        _logger.LogInformation("[Stats] Merged timing for task {TaskId}: Prompt={PromptMs:F0}ms, Gen={GenMs:F0}ms, Total={TotalMs:F0}ms",
            taskId, response.PromptProcessingMs, response.GenerationMs, response.TotalLatencyMs);
    }

    /// <summary>
    /// Merges any available stdout timing data into a RouteResponse.
    /// Used by SendRequestAsync/SendStreamRequestAsync after the HTTP response completes.
    /// </summary>
    private void MergeTimingData(RouteResponse response)
    {
        bool foundInActive = false;

        // Find which task_id this response is associated with
        foreach (var kvp in _activeRequests)
        {
            if (ReferenceEquals(kvp.Value.Response, response))
            {
                var timing = _taskTimings.GetValueOrDefault(kvp.Key);
                if (timing != null)
                    MergeTimingIntoActiveRequest(kvp.Key, timing);
                else
                    _logger.LogWarning("[Stats] Task {TaskId} found in active requests but NO timing data available", kvp.Key);

                // Clean up the active request entry
                _activeRequests.TryRemove(kvp.Key, out _);
                foundInActive = true;
                break;
            }
        }

        if (!foundInActive)
        {
            var activeTaskIds = string.Join(",", _activeRequests.Keys);
            var timingKeys = string.Join(",", _taskTimings.Keys);
            _logger.LogWarning("[Stats] Response NOT found in active requests! Active={_ActiveCount} (tasks: {ActiveTasks}), Pending={_PendingCount}, Timing entries: {TimingEntries}",
                _activeRequests.Count, activeTaskIds, _pendingRequests.Count, timingKeys);
        }

        // Also try to remove from pending queue if it wasn't assigned a task yet
        var itemsToRequeue = new System.Collections.Generic.List<(DateTimeOffset, RouteResponse)>();
        bool foundAndRemoved = false;
        while (_pendingRequests.TryDequeue(out var item))
        {
            if (ReferenceEquals(item.Response, response) && !foundAndRemoved)
                foundAndRemoved = true; // skip this one
            else
                itemsToRequeue.Add(item);
        }
        foreach (var item in itemsToRequeue)
            _pendingRequests.Enqueue(item);
    }
}
