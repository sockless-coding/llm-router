using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Models.OpenAI;

namespace LR.Providers;

/// <summary>
/// Thin orchestrator for llama.cpp-based backend providers.
/// Delegates to specialized components: ArgBuilder, ProcessManager, ResponseParser, TimingCoordinator.
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

    private readonly LlamaCppArgBuilder _argBuilder;
    private readonly LlamaCppProcessManager _processManager;
    private readonly LlamaCppTimingCoordinator _timingCoordinator;

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

    public LlamaCppProvider(
        ILogger<LlamaCppProvider> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;

        // Configure SocketsHttpHandler to handle socket resets gracefully:
        // - Short pooled connection lifetime avoids stale connections (llama.cpp may close idle ones)
        // - Limit max connections per server to avoid overloading
        var handler = new System.Net.Http.SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip,
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = 10,
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
        };

        _httpClient = new HttpClient(handler);
        // Timeout for the entire request (send + receive). Streaming responses use this as a
        // per-chunk timeout — if no data arrives within this window, the read will cancel.
        _httpClient.Timeout = TimeSpan.FromMinutes(5);

        // Initialize sub-components
        var stdoutParser = new LlamaCppStdoutParser();
        _argBuilder = new LlamaCppArgBuilder();

        // Resolve loggers from a scope for the sub-components
        using (var scope = scopeFactory.CreateScope())
        {
            var serviceProvider = scope.ServiceProvider;
            var timingLogger = serviceProvider.GetRequiredService<ILogger<LlamaCppTimingCoordinator>>();
            var processManagerLogger = serviceProvider.GetRequiredService<ILogger<LlamaCppProcessManager>>();

            _timingCoordinator = new LlamaCppTimingCoordinator(timingLogger);
            _processManager = new LlamaCppProcessManager(processManagerLogger, scopeFactory, _timingCoordinator, stdoutParser);
        }
    }

    /// <summary>
    /// Sets the server instance reference for logging purposes.
    /// </summary>
    public void SetServerInstance(ServerInstance? instance)
    {
        _serverInstance = instance;
        _processManager.SetServerInstance(instance);
    }

    /// <summary>
    /// Applies engine-specific configuration from the backend config data.
    /// Override to add provider-specific configuration handling.
    /// </summary>
    public virtual void Configure(BackendConfigData configData)
    {
        ExecutableFolderPath = configData.LlamaCppExecutableFolderPath;
        _processManager.ExecutableFolderPath = ExecutableFolderPath;
        _processManager.CompanionAppPath = configData.CompanionAppPath;
        _processManager.EnvironmentSetupCommand = configData.EnvironmentSetupCommand;
    }

    public virtual void StartPort(int? port)
    {
        if (port.HasValue)
        {
            Port = port.Value;
            _argBuilder.Port = Port;
            _processManager.Port = Port;
        }
    }

    public async Task<bool> StartProcessAsync(ModelPreset preset, int? port = null, Func<StartupProgressEvent, Task>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(ServerExecutablePath))
            throw new InvalidOperationException($"Server executable path is not set. ExecutableFolderPath: '{ExecutableFolderPath}'");

        if (!File.Exists(ServerExecutablePath))
            throw new FileNotFoundException($"Server executable not found at: {ServerExecutablePath}");

        // Update port if provided
        if (port.HasValue)
        {
            Port = port.Value;
            _argBuilder.Port = Port;
            _processManager.Port = Port;
        }

        // Auto-detect GPU backend type from folder name
        GpuBackendType = DetectGpuBackendType(ExecutableFolderPath);

        // Build args via ArgBuilder and start process via ProcessManager
        var args = _argBuilder.Build(preset);
        string argString = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

        await LogProviderMessage(ServerLogLevel.Info,
            $"Starting server on port {Port}. Args: {argString.Substring(0, Math.Min(argString.Length, 200))}{(argString.Length > 200 ? "..." : "")}");

        var result = await _processManager.StartProcessAsync(
            ServerExecutablePath,
            argString,
            onProgress,
            cancellationToken);

        if (result)
            await LogProviderMessage(ServerLogLevel.Info, $"Server started successfully on {ServerUrl}.");

        return result;
    }

    public async Task StopProcessAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Stopping server at {ServerUrl}", ServerUrl);
        await LogProviderMessage(ServerLogLevel.Info, "Server stop initiated.");

        await _processManager.StopAllProcessesAsync(cancellationToken);
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
        const int maxRetries = 2;
        Exception? lastException = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (attempt > 0)
            {
                _logger.LogWarning("[Attempt {Attempt}/{Max}] Retrying non-streaming request to {ServerUrl}",
                    attempt + 1, maxRetries + 1, ServerUrl);
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (int)Math.Pow(2, attempt - 1)), cancellationToken);
            }

            try
            {
                if (string.IsNullOrEmpty(ServerUrl)) return null;

                // Register this request for timing data collection from stdout
                var routeResponse = new RouteResponse();
                _timingCoordinator.EnqueuePending(DateTimeOffset.UtcNow, routeResponse);
                _logger.LogInformation("[Stats] Enqueued non-streaming request.");

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
                LlamaCppResponseParser.ParseRouteResponseInto(jsonDoc.RootElement, routeResponse);

                // Non-streaming request: by the time we get here, stdout parsing should have
                // already captured completion timing. Merge it into our response.
                _logger.LogInformation("[Stats] Before merge - PromptMs={PromptMs}, GenMs={GenMs}, TotalMs={TotalMs}",
                    routeResponse.PromptProcessingMs, routeResponse.GenerationMs, routeResponse.TotalLatencyMs);
                _timingCoordinator.MergeTimingData(routeResponse);
                _logger.LogInformation("[Stats] After merge - PromptMs={PromptMs:F0}, GenMs={GenMs:F0}, TotalMs={TotalMs:F0}, TokensProcessed={Tokens}",
                    routeResponse.PromptProcessingMs, routeResponse.GenerationMs, routeResponse.TotalLatencyMs, routeResponse.PromptTokensProcessed);

                return routeResponse;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (System.Net.Http.HttpRequestException ex) when (ex.InnerException is System.IO.IOException && ServerUrl != null)
            {
                lastException = ex;
                _logger.LogWarning(ex, "Transport error while sending request to {ServerUrl} (attempt {Attempt}/{Max}). Retrying...",
                    ServerUrl, attempt + 1, maxRetries + 1);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ServerUrl != null)
            {
                _logger.LogError(ex, "Request failed to {ServerUrl}", ServerUrl);
                await LogProviderMessage(ServerLogLevel.Error,
                    $"Request failed: {ex.Message}");

                throw;
            }
        }

        if (lastException != null)
        {
            _logger.LogError(lastException, "Non-streaming request failed after {MaxRetries} attempts to {ServerUrl}",
                maxRetries + 1, ServerUrl);
            await LogProviderMessage(ServerLogLevel.Error,
                $"Request failed after retries: {lastException.Message}");

            throw new InvalidOperationException($"Failed to send request to {ServerUrl} after {maxRetries + 1} attempts: {lastException.Message}", lastException);
        }

        return null;
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
        _timingCoordinator.EnqueuePending(DateTimeOffset.UtcNow, streamResponse);
        _logger.LogInformation("[Stats] Enqueued streaming request.");

        const int maxRetries = 2;
        Exception? lastException = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (attempt > 0)
            {
                _logger.LogWarning("[Attempt {Attempt}/{Max}] Retrying streaming request to {ServerUrl}",
                    attempt + 1, maxRetries + 1, ServerUrl);
                // Brief backoff before retry — server may be recovering from a crash/restart
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (int)Math.Pow(2, attempt - 1)), cancellationToken);
            }

            try
            {
                var requestContent = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                _logger.LogInformation("Sending streaming request to {ServerUrl}/v1/chat/completions?stream=true (attempt {Attempt})",
                    ServerUrl, attempt + 1);
                _logger.LogDebug("Streaming request payload: {Payload}", payload);

                // Use SendAsync with ResponseHeadersRead so the call returns as soon as headers arrive,
                // allowing chunks to flow through the stream incrementally instead of buffering
                // the entire response before yielding.
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{ServerUrl}/v1/chat/completions?stream=true")
                {
                    Content = requestContent
                };
                var response = await _httpClient.SendAsync(
                    httpRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new HttpRequestException($"Streaming completion failed: {response.StatusCode} - {errorBody}");
                }

                stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                reader = new StreamReader(stream);
                lastException = null; // Success — clear any previous exception
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (System.Net.Http.HttpRequestException ex) when (ex.InnerException is System.IO.IOException && ServerUrl != null)
            {
                // Connection aborted by server — likely a stale pooled connection or server restart.
                // Error chain: HttpRequestException -> IOException -> SocketException(10053)
                // Transient and retryable.
                lastException = ex;
                _logger.LogWarning(ex, "Transport error while sending request to {ServerUrl} (attempt {Attempt}/{Max}). Retrying...",
                    ServerUrl, attempt + 1, maxRetries + 1);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ServerUrl != null)
            {
                lastException = ex;
                _logger.LogError(ex, "Streaming request failed to {ServerUrl}", ServerUrl);
                await LogProviderMessage(ServerLogLevel.Error,
                    $"Streaming request failed: {ex.Message}");

                throw new InvalidOperationException($"Failed to send streaming request to {ServerUrl}: {ex.Message}", ex);
            }
        }

        // If all retries exhausted, throw with a meaningful error
        if (lastException != null)
        {
            _logger.LogError(lastException, "Streaming request failed after {MaxRetries} attempts to {ServerUrl}",
                maxRetries + 1, ServerUrl);
            await LogProviderMessage(ServerLogLevel.Error,
                $"Streaming request failed after retries: {lastException.Message}");

            throw new InvalidOperationException($"Failed to send streaming request to {ServerUrl} after {maxRetries + 1} attempts: {lastException.Message}", lastException);
        }

        string? accumulatedText = null;
        int reasoningContentChunkCount = 0;
        bool completed = false;

        while (!completed && !cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync();
            if (line is null || string.IsNullOrWhiteSpace(line)) continue;

            // llama.cpp SSE format: "data: {json}"
            if (!line.StartsWith("data:")) continue;

            string data = line.Substring(5).Trim();
            if (string.IsNullOrEmpty(data) || data == "[DONE]") continue;

            // A single SSE frame can carry the last content token AND finish_reason together
            // (common for short answers) — collect both so neither is dropped.
            var chunksToYield = new List<RouteStreamChunk>(2);
            try
            {
                using var jsonDoc = JsonDocument.Parse(data);
                var root = jsonDoc.RootElement;

                // Extract text delta from choices[0].delta.content and reasoning_content
                string? textDelta = null;
                string? reasoningContentDelta = null;
                if (root.TryGetProperty("choices", out JsonElement choices) && choices.GetArrayLength() > 0)
                {
                    var firstChoice = choices[0];
                    if (firstChoice.TryGetProperty("delta", out JsonElement delta))
                    {
                        textDelta = delta.TryGetProperty("content", out JsonElement content)
                            ? content.GetString()
                            : null;

                        // Extract reasoning_content for models with thinking/reasoning capabilities
                        reasoningContentDelta = delta.TryGetProperty("reasoning_content", out JsonElement reasoningContent)
                            ? reasoningContent.GetString()
                            : null;
                    }

                    // Check for finish_reason to detect end of stream
                    string? finishReason = firstChoice.TryGetProperty("finish_reason", out JsonElement fr)
                        ? fr.GetString()
                        : null;

                    if (!string.IsNullOrEmpty(textDelta))
                    {
                        accumulatedText += textDelta;
                        chunksToYield.Add(new RouteStreamChunk { TextDelta = textDelta, ReasoningContentDelta = reasoningContentDelta, IsFinal = false });
                    }
                    else if (!string.IsNullOrEmpty(reasoningContentDelta))
                    {
                        // Yield reasoning content even when there's no regular text delta
                        chunksToYield.Add(new RouteStreamChunk { TextDelta = string.Empty, ReasoningContentDelta = reasoningContentDelta, IsFinal = false });
                    }

                    // Track reasoning content chunks for token counting
                    if (!string.IsNullOrEmpty(reasoningContentDelta))
                    {
                        reasoningContentChunkCount++;
                    }

                    // If finish_reason is set and we have usage data, send final chunk
                    if (finishReason != null)
                    {
                        LlamaCppResponseParser.BuildRouteResponseFromStreamInto(accumulatedText ?? string.Empty, root, streamResponse);
                        streamResponse.ReasoningTokenCount = reasoningContentChunkCount;
                        _logger.LogInformation("[Stats] Stream complete - Before merge PromptMs={PromptMs}, GenMs={GenMs}",
                            streamResponse.PromptProcessingMs, streamResponse.GenerationMs);
                        // Merge timing data from stdout parsing
                        _timingCoordinator.MergeTimingData(streamResponse);
                        _logger.LogInformation("[Stats] Stream complete - After merge PromptMs={PromptMs}, GenMs={GenMs}, TotalMs={TotalMs}",
                            streamResponse.PromptProcessingMs, streamResponse.GenerationMs, streamResponse.TotalLatencyMs);
                        chunksToYield.Add(new RouteStreamChunk { IsFinal = true, Response = streamResponse });
                        completed = true;
                    }
                }
            }
            catch (JsonException)
            {
                // Skip malformed SSE data lines
                continue;
            }

            foreach (var chunk in chunksToYield)
                yield return chunk;
        }

        // If stream ended without a proper finish_reason, send final chunk with what we have
        if (!completed && !string.IsNullOrEmpty(accumulatedText))
        {
            streamResponse.Payload = accumulatedText ?? string.Empty;
            _timingCoordinator.MergeTimingData(streamResponse);
            yield return new RouteStreamChunk { IsFinal = true, Response = streamResponse };
        }
    }

    public virtual string? GetStartCommand(ModelPreset preset, int? port = null)
    {
        if (string.IsNullOrEmpty(ServerExecutablePath) || !File.Exists(ServerExecutablePath))
            return null;

        var args = _argBuilder.Build(preset);
        string argString = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

        // If environment setup is configured, the actual command goes through cmd.exe + batch script
        if (!string.IsNullOrEmpty(_processManager.EnvironmentSetupCommand))
            return $"cmd.exe /c \"call \"\"{_processManager.EnvironmentSetupCommand}\"\" && call \"\"{ServerExecutablePath}\"\" {argString}\"";

        return $"\"{ServerExecutablePath}\" {argString}";
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
    /// Disposes the HTTP client and cancels any running stdout reader.
    /// </summary>
    public void Dispose()
    {
        _processManager.CancelStdoutReader();
        _httpClient.Dispose();
    }
}
