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

    public LlamaCppProvider(
        ILogger<LlamaCppProvider> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
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
        CancellationTokenSource? readCts = null;
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

            // Read both stdout and stderr in linked background tasks (properly tracked)
            var outputLines = new System.Collections.Generic.List<string>();
            readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            Task stdoutReaderTask = Task.Run(async () =>
            {
                try
                {
                    while (!readCts.Token.IsCancellationRequested && _serverProcess != null)
                    {
                        var line = await _serverProcess.StandardOutput.ReadLineAsync();
                        if (line == null) break;
                        lock (outputLines) outputLines.Add(line);
                    }
                }
                catch { /* process exited or stream closed */ }
            }, readCts.Token);

            Task stderrReaderTask = Task.Run(async () =>
            {
                try
                {
                    while (!readCts.Token.IsCancellationRequested && _serverProcess != null)
                    {
                        var line = await _serverProcess.StandardError.ReadLineAsync();
                        if (line == null) break;
                        lock (outputLines) outputLines.Add(line);
                    }
                }
                catch { /* process exited or stream closed */ }
            }, readCts.Token);

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
            // Cancel the stderr reader so it doesn't keep running
            readCts?.Cancel();
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

            _httpClient.Timeout = TimeSpan.FromSeconds(5);
            var response = await _httpClient.GetAsync($"{ServerUrl}/health", cancellationToken);
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
            return ParseRouteResponse(jsonDoc.RootElement);
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
                        var routeResponse = BuildRouteResponseFromStream(accumulatedText ?? string.Empty, root);
                        chunk = new RouteStreamChunk { IsFinal = true, Response = routeResponse };
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
            yield return new RouteStreamChunk { IsFinal = true, Response = new RouteResponse { Payload = accumulatedText } };
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
    /// Parses a non-streaming llama.cpp OpenAI-compatible response into a RouteResponse.
    /// </summary>
    private static RouteResponse ParseRouteResponse(JsonElement root)
    {
        var response = new RouteResponse();

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

        return response;
    }

    /// <summary>
    /// Builds a RouteResponse from streaming metadata.
    /// </summary>
    private static RouteResponse BuildRouteResponseFromStream(string accumulatedText, JsonElement root)
    {
        var response = new RouteResponse { Payload = accumulatedText };

        if (root.TryGetProperty("usage", out JsonElement usage))
        {
            response.PromptTokensProcessed = GetInt32(usage, "prompt_tokens") ?? 0;
            response.GeneratedTokenCount = GetInt32(usage, "completion_tokens") ?? 0;
        }

        return response;
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
    /// Disposes the HTTP client.
    /// </summary>
    public void Dispose()
    {
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
