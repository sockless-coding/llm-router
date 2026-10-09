using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Models.OpenAI;
using LR.Core.Services;
using LR.Core.Wrapper;

namespace LR.Providers;

/// <summary>
/// Shared base for providers whose engine runs as a local HTTP server process supervised by
/// LR.Wrapper and speaks the OpenAI-compatible <c>/v1/chat/completions</c> API (plus, optionally,
/// Anthropic's <c>/v1/messages</c>) with llama.cpp-style <c>/health</c>, <c>/props</c> and
/// <c>/slots</c> endpoints. Handles process start/stop/reconnect, health checks, request sending,
/// SSE parsing and server logging; a concrete engine supplies how to launch it
/// (<see cref="BuildLaunchSpec"/>), how to tell from its output that it's ready
/// (<see cref="IsModelLoadedLine"/>/<see cref="TryParseListeningPort"/>), and optionally
/// per-request hooks and extra request headers.
/// </summary>
public abstract class ManagedServerProviderBase : IBackendProvider, IWrapperDiagnostics, IServerCapacityProvider, IServerOutputHandler, IDisposable
{
    public abstract ServerEngine Engine { get; }

    /// <inheritdoc />
    public int? WrapperPid => _processManager.WrapperPid;

    /// <inheritdoc />
    public int? ServerPid => _processManager.ServerPid;

    /// <summary>
    /// Snapshot of the running server's <c>/props</c> (slot count, context window, modalities,
    /// build info). Null until the first successful health check reads it; reset on stop/restart
    /// so a restart with a different config is re-read.
    /// </summary>
    private volatile LlamaServerProps? _serverProps;

    /// <summary>
    /// Latest snapshot of the running server's Prometheus <c>/metrics</c> (KV-cache usage,
    /// in-flight/deferred request counts). Null until <see cref="RefreshRuntimeUsageAsync"/>
    /// first succeeds; reset on stop/restart.
    /// </summary>
    private volatile LlamaRuntimeUsage? _runtimeUsage;

    /// <inheritdoc />
    public int? MaxConcurrentRequests => _serverProps?.TotalSlots is int n && n > 0 ? n : null;

    /// <inheritdoc />
    public LlamaServerProps? ServerProps => _serverProps;

    /// <inheritdoc />
    public LlamaRuntimeUsage? RuntimeUsage => _runtimeUsage;

    /// <summary>
    /// The engine's install folder, from <see cref="BackendConfigData.InstallFolderPath"/>.
    /// What it must contain is engine-specific — see <see cref="BuildLaunchSpec"/>.
    /// </summary>
    protected string? InstallFolderPath { get; private set; }

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

    private readonly WrapperProcessManager _processManager;

    /// <summary>
    /// HTTP client for communicating with the engine's server.
    /// </summary>
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Logger for this provider instance.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Factory for creating scoped service instances (used to resolve IServerLogService safely from a singleton).
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// The server instance this provider is managing (set during startup for logging purposes).
    /// </summary>
    private ServerInstance? _serverInstance;

    protected ManagedServerProviderBase(
        ILogger logger,
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

        using (var scope = scopeFactory.CreateScope())
        {
            var processManagerLogger = scope.ServiceProvider.GetRequiredService<ILogger<WrapperProcessManager>>();
            _processManager = new WrapperProcessManager(processManagerLogger, scopeFactory, this);
        }
    }

    // --- Engine-specific hooks ---

    /// <summary>
    /// Builds the process launch for <paramref name="preset"/> on <see cref="Port"/>. Throws
    /// (with a user-facing message) if the install folder or preset isn't usable — e.g.
    /// <see cref="InvalidOperationException"/> when nothing is configured,
    /// <see cref="FileNotFoundException"/> when the executable is missing.
    /// </summary>
    protected abstract ServerLaunchSpec BuildLaunchSpec(ModelPreset preset);

    /// <inheritdoc cref="IServerOutputHandler.IsModelLoadedLine"/>
    protected abstract bool IsModelLoadedLine(string line);

    /// <inheritdoc cref="IServerOutputHandler.TryParseListeningPort"/>
    protected abstract int? TryParseListeningPort(string line);

    /// <inheritdoc cref="IServerOutputHandler.OnOutputLine"/>
    protected virtual void OnOutputLine(string line) { }

    /// <summary>
    /// Called with a fresh <see cref="RouteResponse"/> just before a request is sent, so an engine
    /// that reports metrics out-of-band (e.g. llama.cpp's stdout timings) can correlate them.
    /// </summary>
    protected virtual void OnRequestStarting(RouteResponse response) { }

    /// <summary>
    /// Called once the response has been parsed (or the stream has ended), to merge any
    /// out-of-band metrics into it.
    /// </summary>
    protected virtual void OnRequestCompleted(RouteResponse response) { }

    /// <summary>
    /// Adds engine-specific headers (e.g. an API key) to every request sent to the server,
    /// including <c>/props</c> and <c>/slots</c> reads.
    /// </summary>
    protected virtual void ApplyRequestHeaders(HttpRequestMessage request) { }

    /// <summary>
    /// Endpoint for streaming OpenAI-protocol requests. The body always carries
    /// <c>"stream": true</c>; engines that also want it in the query string override this.
    /// </summary>
    protected virtual string OpenAiStreamingEndpoint => "/v1/chat/completions";

    /// <summary>
    /// How long a start may take (process launch through the ready markers) before it's treated
    /// as failed. Engines that load very large models from disk can raise this.
    /// </summary>
    protected virtual TimeSpan StartupTimeout => TimeSpan.FromMinutes(10);

    /// <summary>The server instance this provider manages, once <see cref="SetServerInstance"/> has run.</summary>
    protected ServerInstance? ServerInstance => _serverInstance;

    /// <summary>For resolving scoped services (e.g. <see cref="IPresetManager"/>) from this long-lived provider.</summary>
    protected IServiceScopeFactory ScopeFactory => _scopeFactory;

    void IServerOutputHandler.OnOutputLine(string line) => OnOutputLine(line);
    bool IServerOutputHandler.IsModelLoadedLine(string line) => IsModelLoadedLine(line);
    int? IServerOutputHandler.TryParseListeningPort(string line) => TryParseListeningPort(line);

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
        InstallFolderPath = configData.InstallFolderPath;
        _processManager.CompanionAppPath = configData.CompanionAppPath;
        _processManager.EnvironmentSetupCommand = configData.EnvironmentSetupCommand;
    }

    public async Task<bool> StartProcessAsync(ModelPreset preset, int? port = null, Func<StartupProgressEvent, Task>? onProgress = null, CancellationToken cancellationToken = default)
    {
        // Update port if provided — before building the launch, which bakes the port in.
        if (port.HasValue)
        {
            Port = port.Value;
            _processManager.Port = Port;
        }

        var launch = BuildLaunchSpec(preset);
        _processManager.StartupTimeout = StartupTimeout;

        // Forget the previous run's /props and /metrics — this start may use a different config.
        _serverProps = null;
        _runtimeUsage = null;

        string argPreview = string.Join(" ", launch.Arguments);

        await LogProviderMessage(ServerLogLevel.Info,
            $"Starting server on port {Port}. Args: {argPreview.Substring(0, Math.Min(argPreview.Length, 200))}{(argPreview.Length > 200 ? "..." : "")}");

        var result = await _processManager.StartProcessAsync(
            launch,
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

        _serverProps = null;
        _runtimeUsage = null;

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
            if (!response.IsSuccessStatusCode)
                return false;

            // Now that the server is up, read its /props once — slot count (the resolved -np,
            // used by the router to cap concurrency), context window, modalities, build info.
            if (_serverProps is null)
                await TryRefreshServerPropsAsync(httpClient, cancellationToken);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort read of the server's llama.cpp-style <c>/props</c> endpoint. Any failure is
    /// swallowed — the server is already known healthy and callers fall back to preset/GGUF data
    /// until this succeeds on a later health check.
    /// </summary>
    private async Task TryRefreshServerPropsAsync(HttpClient httpClient, CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, "/props");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            int? totalSlots = root.TryGetProperty("total_slots", out var slots) && slots.TryGetInt32(out var s) && s > 0
                ? s : null;

            int? ctxPerSlot = root.TryGetProperty("default_generation_settings", out var gen) &&
                              gen.ValueKind == JsonValueKind.Object &&
                              gen.TryGetProperty("n_ctx", out var nctx) && nctx.TryGetInt32(out var c) && c > 0
                ? c : null;

            bool? vision = null, audio = null;
            if (root.TryGetProperty("modalities", out var mods) && mods.ValueKind == JsonValueKind.Object)
            {
                if (mods.TryGetProperty("vision", out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                    vision = v.GetBoolean();
                if (mods.TryGetProperty("audio", out var a) && (a.ValueKind == JsonValueKind.True || a.ValueKind == JsonValueKind.False))
                    audio = a.GetBoolean();
            }

            string? modelPath = root.TryGetProperty("model_path", out var mp) && mp.ValueKind == JsonValueKind.String
                ? mp.GetString() : null;
            string? buildInfo = root.TryGetProperty("build_info", out var bi) && bi.ValueKind == JsonValueKind.String
                ? bi.GetString() : null;

            _serverProps = new LlamaServerProps
            {
                TotalSlots = totalSlots,
                ContextSizePerSlot = ctxPerSlot,
                Vision = vision,
                Audio = audio,
                ModelPath = modelPath,
                BuildInfo = buildInfo,
            };

            await LogProviderMessage(ServerLogLevel.Info,
                $"Server /props: {(totalSlots?.ToString() ?? "?")} slot(s), ctx/slot {(ctxPerSlot?.ToString() ?? "?")}" +
                $"{(vision == true ? ", vision" : "")}{(audio == true ? ", audio" : "")}.");
        }
        catch
        {
            // Ignore — will retry on the next health check.
        }
    }

    /// <summary>
    /// Best-effort read of the server's llama.cpp-style <c>/slots</c> endpoint. Populates <see cref="RuntimeUsage"/>
    /// with per-slot context (KV-cache) occupancy — <c>n_ctx</c> and the tokens each slot is
    /// currently holding (cached prompt + generated so far). llama.cpp's Prometheus
    /// <c>/metrics</c> no longer exposes a KV-cache gauge, and <c>/slots</c> is on by default and
    /// carries the raw numbers, so this is where the figure comes from. Any failure is swallowed
    /// and the previous snapshot is left in place.
    /// </summary>
    public async Task RefreshRuntimeUsageAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(ServerUrl))
            return;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            using var request = CreateRequest(HttpMethod.Get, "/slots");
            using var response = await _httpClient.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
                return;

            string body = await response.Content.ReadAsStringAsync(cts.Token);
            if (LlamaSlotsSnapshot.FromJson(body) is { } usage)
                _runtimeUsage = usage;
        }
        catch
        {
            // Ignore — will retry on the next tick; the previous snapshot stays in place.
        }
    }

    public async Task<RouteResponse?> SendRequestAsync(string payload, ApiProtocol protocol = ApiProtocol.OpenAI, CancellationToken cancellationToken = default)
    {
        const int maxRetries = 2;
        Exception? lastException = null;

        // The engine exposes a native Anthropic-compatible endpoint alongside its OpenAI one, so
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

                var routeResponse = new RouteResponse();
                OnRequestStarting(routeResponse);

                // This method reads the entire response body as one JSON document, so the
                // backend must not switch to SSE framing ("data: {...}\n\n") — force "stream"
                // off even if the original client payload (e.g. one that sat in the queue
                // after being built for a streaming request) still has it set to true.
                var requestBody = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payload)
                    ?? new Dictionary<string, JsonElement>();
                requestBody["stream"] = JsonSerializer.SerializeToElement(false);

                using var httpRequest = CreateRequest(HttpMethod.Post, endpoint);
                httpRequest.Content = JsonContent.Create(requestBody);
                using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

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

                OnRequestCompleted(routeResponse);

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
                // A context-exceeded response from llama.cpp is a routine, user-actionable
                // condition (the request just doesn't fit the model's context window), not a
                // provider malfunction — log it at Warning so it doesn't read like an outage.
                bool contextExceeded = BackendErrorClassifier.IsContextExceeded(ex);
                var logLevel = contextExceeded ? LogLevel.Warning : LogLevel.Error;
                _logger.Log(logLevel, ex, "Request failed to {ServerUrl}", ServerUrl);
                await LogProviderMessage(contextExceeded ? ServerLogLevel.Warning : ServerLogLevel.Error,
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

        var streamResponse = new RouteResponse();
        OnRequestStarting(streamResponse);

        // Whatever the original client asked for, this path reads SSE — make sure the body says
        // so (a payload queued from a non-streaming request may still have "stream": false).
        var streamBody = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payload)
            ?? new Dictionary<string, JsonElement>();
        streamBody["stream"] = JsonSerializer.SerializeToElement(true);
        payload = JsonSerializer.Serialize(streamBody);

        const int maxRetries = 2;
        Exception? lastException = null;

        string endpoint = protocol == ApiProtocol.Claude ? "/v1/messages" : OpenAiStreamingEndpoint;

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
                httpRequest = CreateRequest(HttpMethod.Post, endpoint);
                httpRequest.Content = requestContent;
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

                // A context-exceeded response from llama.cpp is a routine, user-actionable
                // condition (the request just doesn't fit the model's context window), not a
                // provider malfunction — log it at Warning so it doesn't read like an outage,
                // and callers (the API handlers) are responsible for turning it into a clean
                // client-facing error rather than an unhandled exception.
                bool contextExceeded = BackendErrorClassifier.IsContextExceeded(ex);
                var logLevel = contextExceeded ? LogLevel.Warning : LogLevel.Error;
                _logger.Log(logLevel, ex, "Streaming request failed to {ServerUrl}", ServerUrl);
                await LogProviderMessage(contextExceeded ? ServerLogLevel.Warning : ServerLogLevel.Error,
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
        // Tool-call indexes whose opening delta (the one carrying id/type/name) has already been
        // forwarded to the client. Until a call's name is known we buffer its fragments instead
        // of streaming them — see the tool_calls handling below.
        var toolCallOpenerEmitted = new HashSet<int>();

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
                OnRequestCompleted(streamResponse);
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
                        //
                        // We also can't forward a call's fragments until its function name is
                        // known: under speculative/MTP decoding llama.cpp's OpenAI stream
                        // intermittently emits a call whose arguments come through but whose name
                        // never does. A client that stored that would replay an un-dispatchable
                        // tool call ("the tool \"\" does not exist") on every subsequent turn. So
                        // buffer each call's fragments into `entry` until the name arrives, then
                        // emit one opening delta carrying the id/type/name plus everything
                        // accumulated so far; stream the rest normally after that. If the name
                        // never arrives, nothing is emitted and FinalizeToolCalls drops the call.
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

                                string frameName = string.Empty;
                                string frameArgs = string.Empty;
                                if (tc.TryGetProperty("id", out JsonElement idEl) && idEl.GetString() is { } id)
                                    entry.Id = id;
                                if (tc.TryGetProperty("type", out JsonElement typeEl) && typeEl.GetString() is { } type)
                                    entry.Type = type;
                                if (tc.TryGetProperty("function", out JsonElement fnEl))
                                {
                                    if (fnEl.TryGetProperty("name", out JsonElement nameEl) && nameEl.GetString() is { } name)
                                    {
                                        entry.Function.Name += name;
                                        frameName = name;
                                    }
                                    if (fnEl.TryGetProperty("arguments", out JsonElement argEl) && argEl.GetString() is { } args)
                                    {
                                        entry.Function.Arguments += args;
                                        frameArgs = args;
                                    }
                                }

                                // Name not yet known — keep buffering, emit nothing for this call.
                                if (string.IsNullOrWhiteSpace(entry.Function.Name))
                                    continue;

                                if (toolCallOpenerEmitted.Add(index))
                                {
                                    // First forwardable frame for this call: send the full state
                                    // captured so far (covers any frames we buffered while the
                                    // name was still missing).
                                    toolCallDeltas.Add(new ChatToolCall
                                    {
                                        Index = index,
                                        Id = entry.Id,
                                        Type = entry.Type,
                                        Function = new ChatToolCallFunction
                                        {
                                            Name = entry.Function.Name,
                                            Arguments = entry.Function.Arguments
                                        }
                                    });
                                }
                                else
                                {
                                    // Opener already sent — forward just this frame's fragment.
                                    toolCallDeltas.Add(new ChatToolCall
                                    {
                                        Index = index,
                                        Id = entry.Id,
                                        Type = entry.Type,
                                        Function = new ChatToolCallFunction { Name = frameName, Arguments = frameArgs }
                                    });
                                }
                            }

                            // Every call in this frame is still nameless (buffered) — treat it as
                            // "no tool-call delta here" rather than yielding an empty array.
                            if (toolCallDeltas.Count == 0)
                                toolCallDeltas = null;
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
            OnRequestCompleted(streamResponse);
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
                        OnRequestCompleted(streamResponse);
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
            OnRequestCompleted(streamResponse);
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
        ServerLaunchSpec launch;
        try
        {
            launch = BuildLaunchSpec(preset);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return null;
        }

        string argString = WindowsCommandLine.Join(launch.Arguments);

        // If environment setup is configured, the actual command runs as a two-line batch
        // script (see WrapperHost.CreateTempBatchScriptAsync) — mirror that here rather than
        // trying to flatten it into a single cmd.exe /c line, which needs another layer of
        // quoting and would no longer match what's actually executed.
        if (!string.IsNullOrEmpty(_processManager.EnvironmentSetupCommand))
            return $"call {_processManager.EnvironmentSetupCommand}\r\ncall \"{launch.ExecutablePath}\" {argString}";

        return $"\"{launch.ExecutablePath}\" {argString}";
    }

    /// <summary>
    /// Creates a request to <paramref name="path"/> on the running server with
    /// <see cref="ApplyRequestHeaders"/> applied.
    /// </summary>
    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{ServerUrl}{path}");
        ApplyRequestHeaders(request);
        return request;
    }

    /// <summary>
    /// Disposes the HTTP client and cancels any running stdout reader.
    /// </summary>
    public virtual void Dispose()
    {
        _processManager.CancelStdoutReader();
        _httpClient.Dispose();
    }
}
