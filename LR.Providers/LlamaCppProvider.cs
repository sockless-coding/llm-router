using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Models.OpenAI;
using LR.Core.Wrapper;

namespace LR.Providers;

/// <summary>
/// Thin orchestrator for llama.cpp-based backend providers.
/// Delegates to specialized components: ArgBuilder, ProcessManager, ResponseParser, TimingCoordinator.
/// </summary>
public class LlamaCppProvider : IBackendProvider, IWrapperDiagnostics, IDisposable
{
    public ServerEngine Engine => ServerEngine.LlamaCpp;

    /// <inheritdoc />
    public int? WrapperPid => _processManager.WrapperPid;

    /// <inheritdoc />
    public int? ServerPid => _processManager.ServerPid;

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
    /// Uses the literal loopback address rather than "localhost": llama.cpp binds IPv4-only
    /// (127.0.0.1) by default, but "localhost" resolves to ::1 first on Windows, which causes
    /// every connection (health checks and completion requests alike) to eat several failed
    /// IPv6 connection attempts before falling back to IPv4 — several seconds of pure overhead
    /// per request that real Ollama doesn't have, since it listens on both stacks.
    /// </summary>
    protected virtual string? ServerUrl => $"http://127.0.0.1:{Port}";

    /// <summary>
    /// The GPU backend type this llama.cpp build was compiled for (e.g., CUDA, Vulkan, SYCL).
    /// Can be auto-detected from the folder name or set explicitly.
    /// </summary>
    protected BackendType? GpuBackendType { get; set; }

    private readonly LlamaCppArgBuilder _argBuilder;
    private readonly WrapperProcessManager _processManager;
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
            var processManagerLogger = serviceProvider.GetRequiredService<ILogger<WrapperProcessManager>>();

            _timingCoordinator = new LlamaCppTimingCoordinator(timingLogger);
            _processManager = new WrapperProcessManager(processManagerLogger, scopeFactory, _timingCoordinator, stdoutParser);
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
        string argPreview = string.Join(" ", args);

        await LogProviderMessage(ServerLogLevel.Info,
            $"Starting server on port {Port}. Args: {argPreview.Substring(0, Math.Min(argPreview.Length, 200))}{(argPreview.Length > 200 ? "..." : "")}");

        var result = await _processManager.StartProcessAsync(
            ServerExecutablePath,
            args,
            onProgress,
            cancellationToken);

        if (result)
            await LogProviderMessage(ServerLogLevel.Info, $"Server started successfully on {ServerUrl}.");

        return result;
    }

    /// <summary>
    /// Restarts the server with a new preset without disturbing the companion app — the wrapper's
    /// start command is idempotent with respect to the companion, so this is identical to
    /// <see cref="StartProcessAsync"/> under the hood.
    /// </summary>
    public Task<bool> RestartProcessAsync(ModelPreset preset, int? port = null, Func<StartupProgressEvent, Task>? onProgress = null, CancellationToken cancellationToken = default)
        => StartProcessAsync(preset, port, onProgress, cancellationToken);

    public async Task<bool> TryReconnectAsync(CancellationToken cancellationToken = default)
    {
        bool reconnected = await _processManager.TryReconnectAsync(cancellationToken);
        if (reconnected)
        {
            Port = _processManager.Port;
            await LogProviderMessage(ServerLogLevel.Info, $"Reattached to an already-running server on {ServerUrl}.");
        }
        return reconnected;
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

    public async Task<RouteResponse?> SendRequestAsync(string payload, ApiProtocol protocol = ApiProtocol.OpenAI, CancellationToken cancellationToken = default)
    {
        const int maxRetries = 2;
        Exception? lastException = null;

        // llama.cpp exposes a native Anthropic-compatible endpoint alongside its OpenAI one, so
        // a Claude-protocol payload (as built by ClaudeHandler) is routed there unchanged instead
        // of being sent to /v1/chat/completions, which doesn't understand its shape (top-level
        // "system", content blocks, etc.).
        string endpoint = protocol == ApiProtocol.Claude ? "/v1/messages" : "/v1/chat/completions";

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
                    $"{ServerUrl}{endpoint}",
                    JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payload),
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new HttpRequestException($"Chat completion failed: {response.StatusCode} - {errorBody}");
                }

                var jsonDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (protocol == ApiProtocol.Claude)
                    LlamaCppClaudeResponseParser.ParseRouteResponseInto(jsonDoc.RootElement, routeResponse);
                else
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
        string payload, ApiProtocol protocol = ApiProtocol.OpenAI, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(ServerUrl)) yield break;

        Stream? stream = null;
        StreamReader? reader = null;
        HttpResponseMessage? response = null;
        HttpRequestMessage? httpRequest = null;

        // Register this request for timing data collection from stdout
        var streamResponse = new RouteResponse();
        _timingCoordinator.EnqueuePending(DateTimeOffset.UtcNow, streamResponse);
        _logger.LogInformation("[Stats] Enqueued streaming request.");

        const int maxRetries = 2;
        Exception? lastException = null;

        // llama.cpp's native Anthropic endpoint takes "stream" as a body field (like the real
        // Claude API), not a query string — unlike the ?stream=true convention used below for
        // its OpenAI-compatible endpoint.
        string endpoint = protocol == ApiProtocol.Claude ? "/v1/messages" : "/v1/chat/completions?stream=true";

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
                _logger.LogInformation("Sending streaming request to {ServerUrl}{Endpoint} (attempt {Attempt})",
                    ServerUrl, endpoint, attempt + 1);
                _logger.LogDebug("Streaming request payload: {Payload}", payload);

                // Use SendAsync with ResponseHeadersRead so the call returns as soon as headers arrive,
                // allowing chunks to flow through the stream incrementally instead of buffering
                // the entire response before yielding.
                httpRequest?.Dispose(); // dispose a prior attempt's request, if this is a retry
                httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{ServerUrl}{endpoint}")
                {
                    Content = requestContent
                };
                response = await _httpClient.SendAsync(
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

        try
        {
        if (protocol == ApiProtocol.Claude)
        {
            await foreach (var claudeChunk in ReadClaudeSseStreamAsync(reader!, streamResponse, cancellationToken))
                yield return claudeChunk;
            yield break;
        }

        string? accumulatedText = null;
        int reasoningContentChunkCount = 0;
        bool completed = false;
        var accumulatedToolCalls = new Dictionary<int, ChatToolCall>();

        while (!completed)
        {
            string? line;
            try
            {
                line = await reader!.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Client disconnected, or the backend timeout elapsed. Stop reading immediately
                // rather than waiting on llama.cpp for more tokens — the finally block below
                // disposes the response/stream, which tears down the TCP connection to
                // llama.cpp so it observes the dropped peer and stops generating, instead of
                // continuing to burn GPU time on a response nobody will receive.
                break;
            }
            // EOF: the backend closed the connection. ReadLineAsync keeps returning null
            // instantly on a dead stream, so looping back here would spin forever instead
            // of blocking — stop and let the post-loop fallback flush whatever we have.
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            // llama.cpp SSE format: "data: {json}"
            if (!line.StartsWith("data:")) continue;

            string data = line.Substring(5).Trim();
            if (string.IsNullOrEmpty(data)) continue;

            if (data == "[DONE]")
            {
                // Official end of stream. Flush the final chunk now — this also covers the
                // case where usage arrived in a separate trailing frame after finish_reason
                // (llama.cpp/OpenAI send stream_options usage as its own frame with empty
                // choices, which the code below folds into streamResponse as it's seen).
                streamResponse.ReasoningTokenCount = reasoningContentChunkCount;
                streamResponse.ToolCalls = FinalizeToolCalls(accumulatedToolCalls);
                _timingCoordinator.MergeTimingData(streamResponse);
                completed = true;
                yield return new RouteStreamChunk { IsFinal = true, Response = streamResponse };
                break;
            }

            // A single SSE frame can carry the last content token AND finish_reason together
            // (common for short answers) — collect both so neither is dropped.
            var chunksToYield = new List<RouteStreamChunk>(2);
            try
            {
                using var jsonDoc = JsonDocument.Parse(data);
                var root = jsonDoc.RootElement;

                bool hasChoices = root.TryGetProperty("choices", out JsonElement choices) && choices.GetArrayLength() > 0;

                if (hasChoices)
                {
                    // Extract text delta from choices[0].delta.content and reasoning_content
                    string? textDelta = null;
                    string? reasoningContentDelta = null;
                    List<ChatToolCall>? toolCallDeltas = null;
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

                        // Tool calls stream incrementally, index-keyed, the same way OpenAI does:
                        // each frame carries a fragment of one call's id/name/arguments. Some
                        // llama.cpp versions include an empty "tool_calls": [] on frames that
                        // aren't actually part of a tool call — skip those so we don't yield a
                        // non-null-but-empty ToolCallDeltas that downstream code (and clients
                        // accumulating history) could mistake for "this message has tool_calls".
                        if (delta.TryGetProperty("tool_calls", out JsonElement toolCallsDelta) && toolCallsDelta.ValueKind == JsonValueKind.Array && toolCallsDelta.GetArrayLength() > 0)
                        {
                            toolCallDeltas = new List<ChatToolCall>();
                            foreach (var tc in toolCallsDelta.EnumerateArray())
                            {
                                int index = tc.TryGetProperty("index", out JsonElement idxEl) ? idxEl.GetInt32() : 0;
                                if (!accumulatedToolCalls.TryGetValue(index, out var entry))
                                {
                                    accumulatedToolCalls[index] = entry = new ChatToolCall { Function = new ChatToolCallFunction() };
                                }

                                if (tc.TryGetProperty("id", out JsonElement idEl) && idEl.GetString() is { } id)
                                    entry.Id = id;
                                if (tc.TryGetProperty("type", out JsonElement typeEl) && typeEl.GetString() is { } type)
                                    entry.Type = type;
                                if (tc.TryGetProperty("function", out JsonElement fnEl))
                                {
                                    if (fnEl.TryGetProperty("name", out JsonElement nameEl) && nameEl.GetString() is { } name)
                                        entry.Function.Name += name;
                                    if (fnEl.TryGetProperty("arguments", out JsonElement argEl) && argEl.GetString() is { } args)
                                        entry.Function.Arguments += args;
                                }

                                toolCallDeltas.Add(new ChatToolCall
                                {
                                    Index = index,
                                    Id = entry.Id,
                                    Type = entry.Type,
                                    Function = new ChatToolCallFunction
                                    {
                                        Name = tc.TryGetProperty("function", out JsonElement fnDeltaEl) && fnDeltaEl.TryGetProperty("name", out JsonElement nameDeltaEl) ? nameDeltaEl.GetString() ?? string.Empty : string.Empty,
                                        Arguments = tc.TryGetProperty("function", out JsonElement fnArgEl) && fnArgEl.TryGetProperty("arguments", out JsonElement argDeltaEl) ? argDeltaEl.GetString() ?? string.Empty : string.Empty
                                    }
                                });
                            }
                        }
                    }

                    // Check for finish_reason to detect end of stream
                    string? finishReason = firstChoice.TryGetProperty("finish_reason", out JsonElement fr)
                        ? fr.GetString()
                        : null;

                    if (!string.IsNullOrEmpty(textDelta) || toolCallDeltas is not null)
                    {
                        accumulatedText += textDelta;
                        chunksToYield.Add(new RouteStreamChunk { TextDelta = textDelta ?? string.Empty, ReasoningContentDelta = reasoningContentDelta, ToolCallDeltas = toolCallDeltas, IsFinal = false });
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

                    // finish_reason marks the end of content, but usage/timings may still
                    // arrive in a later frame — capture what's here and keep reading until
                    // [DONE] instead of finalizing immediately.
                    if (finishReason != null)
                    {
                        LlamaCppResponseParser.BuildRouteResponseFromStreamInto(accumulatedText ?? string.Empty, root, streamResponse);
                        streamResponse.FinishReason = finishReason;
                        _logger.LogInformation("[Stats] Stream finish_reason seen - PromptMs={PromptMs}, GenMs={GenMs}",
                            streamResponse.PromptProcessingMs, streamResponse.GenerationMs);
                    }
                }
                else if (root.TryGetProperty("usage", out _))
                {
                    // Trailing usage-only frame (choices: []), sent when stream_options.include_usage
                    // was requested. Merge it in so token counts make it back to the client.
                    LlamaCppResponseParser.BuildRouteResponseFromStreamInto(accumulatedText ?? string.Empty, root, streamResponse);
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

        // Stream ended without a proper [DONE] frame (backend crash, dropped connection, etc.) —
        // surface whatever partial content/reasoning we captured instead of dropping it silently.
        if (!completed)
        {
            streamResponse.ReasoningTokenCount = reasoningContentChunkCount;
            streamResponse.Payload = accumulatedText ?? string.Empty;
            streamResponse.ToolCalls = FinalizeToolCalls(accumulatedToolCalls);
            _timingCoordinator.MergeTimingData(streamResponse);
            yield return new RouteStreamChunk { IsFinal = true, Response = streamResponse };
        }
        }
        finally
        {
            // Dispose eagerly (rather than relying on GC/finalizers) so a cancelled request
            // actually closes the socket to llama.cpp right away instead of leaving the
            // connection — and llama.cpp's in-progress generation — alive indefinitely.
            reader?.Dispose();
            stream?.Dispose();
            response?.Dispose();
            httpRequest?.Dispose();
        }
    }

    /// <summary>
    /// Reads llama.cpp's native Anthropic-compatible SSE stream (from /v1/messages) and yields
    /// normalized RouteStreamChunks, mirroring what the OpenAI-shaped loop above does for
    /// /v1/chat/completions. Anthropic's event framing differs from OpenAI's: named events
    /// (message_start, content_block_delta, message_delta, message_stop, ...) instead of a flat
    /// sequence of "choices" deltas terminated by a literal "[DONE]" marker.
    /// </summary>
    private async IAsyncEnumerable<RouteStreamChunk> ReadClaudeSseStreamAsync(
        StreamReader reader, RouteResponse streamResponse, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? accumulatedText = null;
        string? accumulatedThinking = null;
        bool completed = false;
        var accumulatedToolCalls = new Dictionary<int, ChatToolCall>();

        while (!completed)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Client disconnected, or the backend timeout elapsed — stop reading; the
                // caller's finally block tears down the connection to llama.cpp.
                break;
            }
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Anthropic SSE frames carry both "event: <type>" and "data: {json}" lines; the type
            // is also present in the JSON payload itself, so the "event:" line can be skipped.
            if (!line.StartsWith("data:")) continue;

            string data = line.Substring(5).Trim();
            if (string.IsNullOrEmpty(data)) continue;

            _logger.LogDebug("[Claude SSE] {Data}", data);

            var chunksToYield = new List<RouteStreamChunk>(1);
            try
            {
                using var jsonDoc = JsonDocument.Parse(data);
                var root = jsonDoc.RootElement;
                string? eventType = root.TryGetProperty("type", out JsonElement typeEl) ? typeEl.GetString() : null;
                int blockIndex = root.TryGetProperty("index", out JsonElement indexEl) && indexEl.ValueKind == JsonValueKind.Number
                    ? indexEl.GetInt32()
                    : 0;

                switch (eventType)
                {
                    case "message_start":
                        if (root.TryGetProperty("message", out JsonElement message) &&
                            message.TryGetProperty("usage", out JsonElement startUsage) &&
                            startUsage.TryGetProperty("input_tokens", out JsonElement inputTokensEl) &&
                            inputTokensEl.ValueKind == JsonValueKind.Number)
                        {
                            streamResponse.PromptTokensProcessed = inputTokensEl.GetInt32();
                        }
                        break;

                    case "content_block_start":
                        if (root.TryGetProperty("content_block", out JsonElement contentBlock) &&
                            contentBlock.TryGetProperty("type", out JsonElement blockTypeEl) &&
                            blockTypeEl.GetString() == "tool_use")
                        {
                            string id = contentBlock.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                            string name = contentBlock.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;

                            var call = new ChatToolCall
                            {
                                Index = blockIndex,
                                Id = id,
                                Type = "function",
                                Function = new ChatToolCallFunction { Name = name, Arguments = string.Empty }
                            };
                            accumulatedToolCalls[blockIndex] = call;
                            chunksToYield.Add(new RouteStreamChunk
                            {
                                TextDelta = string.Empty,
                                ToolCallDeltas = new List<ChatToolCall> { call },
                                IsFinal = false
                            });
                        }
                        break;

                    case "content_block_delta":
                        if (root.TryGetProperty("delta", out JsonElement delta) &&
                            delta.TryGetProperty("type", out JsonElement deltaTypeEl))
                        {
                            string? deltaType = deltaTypeEl.GetString();
                            if (deltaType == "text_delta" && delta.TryGetProperty("text", out JsonElement textEl))
                            {
                                string text = textEl.GetString() ?? string.Empty;
                                accumulatedText += text;
                                chunksToYield.Add(new RouteStreamChunk { TextDelta = text, IsFinal = false });
                            }
                            else if (deltaType == "thinking_delta" && delta.TryGetProperty("thinking", out JsonElement thinkingEl))
                            {
                                string thinking = thinkingEl.GetString() ?? string.Empty;
                                accumulatedThinking += thinking;
                                streamResponse.ReasoningTokenCount++;
                                chunksToYield.Add(new RouteStreamChunk { TextDelta = string.Empty, ReasoningContentDelta = thinking, IsFinal = false });
                            }
                            else if (deltaType == "input_json_delta" && delta.TryGetProperty("partial_json", out JsonElement partialJsonEl))
                            {
                                string fragment = partialJsonEl.GetString() ?? string.Empty;
                                if (accumulatedToolCalls.TryGetValue(blockIndex, out var entry))
                                {
                                    entry.Function.Arguments += fragment;
                                    var deltaCall = new ChatToolCall
                                    {
                                        Index = blockIndex,
                                        Id = string.Empty,
                                        Type = "function",
                                        Function = new ChatToolCallFunction { Name = string.Empty, Arguments = fragment }
                                    };
                                    chunksToYield.Add(new RouteStreamChunk
                                    {
                                        TextDelta = string.Empty,
                                        ToolCallDeltas = new List<ChatToolCall> { deltaCall },
                                        IsFinal = false
                                    });
                                }
                            }
                        }
                        break;

                    case "message_delta":
                        if (root.TryGetProperty("delta", out JsonElement msgDelta) &&
                            msgDelta.TryGetProperty("stop_reason", out JsonElement stopReasonEl) &&
                            stopReasonEl.ValueKind == JsonValueKind.String)
                        {
                            streamResponse.FinishReason = stopReasonEl.GetString();
                        }
                        if (root.TryGetProperty("usage", out JsonElement deltaUsage) &&
                            deltaUsage.TryGetProperty("output_tokens", out JsonElement outputTokensEl) &&
                            outputTokensEl.ValueKind == JsonValueKind.Number)
                        {
                            streamResponse.GeneratedTokenCount = outputTokensEl.GetInt32();
                        }
                        break;

                    case "message_stop":
                        streamResponse.Payload = accumulatedText ?? string.Empty;
                        streamResponse.ReasoningContent = accumulatedThinking;
                        streamResponse.ToolCalls = FinalizeToolCalls(accumulatedToolCalls);
                        _timingCoordinator.MergeTimingData(streamResponse);
                        completed = true;
                        chunksToYield.Add(new RouteStreamChunk { IsFinal = true, Response = streamResponse });
                        break;

                    case "error":
                        _logger.LogWarning("[Claude SSE] Backend returned an error event: {Data}", data);
                        break;

                    // content_block_stop, ping: no normalized chunk needed.
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

        // Stream ended without a message_stop frame (backend crash, dropped connection, etc.) —
        // surface whatever partial content we captured instead of dropping it silently.
        if (!completed)
        {
            streamResponse.Payload = accumulatedText ?? string.Empty;
            streamResponse.ReasoningContent = accumulatedThinking;
            streamResponse.ToolCalls = FinalizeToolCalls(accumulatedToolCalls);
            _timingCoordinator.MergeTimingData(streamResponse);
            yield return new RouteStreamChunk { IsFinal = true, Response = streamResponse };
        }
    }

    /// <summary>
    /// Builds the final tool-call list from accumulated stream deltas, dropping any entry whose
    /// function name never came through (empty/whitespace). An empty name is never a valid call —
    /// no client can dispatch it — and observed cases (e.g. under speculative/MTP decoding) show
    /// the model occasionally emitting a tool_calls entry with arguments but no name. Forwarding
    /// that verbatim just moves the failure to the client in a more confusing form (e.g. "Model
    /// tried to call unavailable tool ''"), so it's filtered out here instead.
    /// </summary>
    private static List<ChatToolCall>? FinalizeToolCalls(Dictionary<int, ChatToolCall> accumulated)
    {
        var calls = accumulated.OrderBy(kv => kv.Key)
            .Select(kv => kv.Value)
            .Where(tc => !string.IsNullOrWhiteSpace(tc.Function.Name))
            .ToList();

        return calls.Count > 0 ? calls : null;
    }

    public virtual string? GetStartCommand(ModelPreset preset, int? port = null)
    {
        if (string.IsNullOrEmpty(ServerExecutablePath) || !File.Exists(ServerExecutablePath))
            return null;

        var args = _argBuilder.Build(preset);
        string argString = WindowsCommandLine.Join(args);

        // If environment setup is configured, the actual command runs as a two-line batch
        // script (see WrapperHost.CreateTempBatchScriptAsync) — mirror that here rather than
        // trying to flatten it into a single cmd.exe /c line, which needs another layer of
        // quoting and would no longer match what's actually executed.
        if (!string.IsNullOrEmpty(_processManager.EnvironmentSetupCommand))
            return $"call {_processManager.EnvironmentSetupCommand}\r\ncall \"{ServerExecutablePath}\" {argString}";

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
        if (lower.Contains("rocm") || lower.Contains("hip")) return BackendType.Hip;
        if (lower.Contains("metal")) return BackendType.Metal;
        if (lower.Contains("opencl") || lower.Contains("adreno")) return BackendType.OpenCL;
        if (lower.Contains("openvino")) return BackendType.OpenVino;
        if (lower.Contains("musa")) return BackendType.Musa;
        if (lower.Contains("cann") || lower.Contains("ascend")) return BackendType.Cann;

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
