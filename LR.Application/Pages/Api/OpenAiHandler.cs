using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Models.OpenAI;
using LR.Core.Services;

namespace LR.Application.Pages.Api;

/// <summary>
/// OpenAI-compatible chat completions API handler.
/// Maps /v1/chat/completions and /v1/models endpoints.
/// </summary>
public class OpenAiHandler : IProtocolHandler
{
    // Omit null fields when forwarding to backends — llama.cpp rejects "name":null etc.
    private static readonly JsonSerializerOptions BackendJsonOpts = new() { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly ILogger<OpenAiHandler> _logger;
    private readonly IServerManager _serverManager;
    private readonly IPresetManager _presetManager;
    private readonly IRoutingEngine _routingEngine;
    private readonly IRequestQueueService _queue;
    private readonly IStatisticsService _statisticsService;
    private readonly IChatTemplateVariableExtractor _templateVariableExtractor;
    private readonly IApiRequestLogger _requestLogger;
    private readonly GatewaySettings _gatewaySettings;
    private readonly IApiKeyRequestContext _apiKeyContext;

    public ApiProtocol Protocol => ApiProtocol.OpenAI;
    public string PathPrefix => "/v1";

    public OpenAiHandler(
        ILogger<OpenAiHandler> logger,
        IServerManager serverManager,
        IPresetManager presetManager,
        IRoutingEngine routingEngine,
        IRequestQueueService queue,
        IStatisticsService statisticsService,
        IChatTemplateVariableExtractor templateVariableExtractor,
        IApiRequestLogger requestLogger,
        GatewaySettings gatewaySettings,
        IApiKeyRequestContext apiKeyContext)
    {
        _logger = logger;
        _serverManager = serverManager;
        _presetManager = presetManager;
        _routingEngine = routingEngine;
        _queue = queue;
        _statisticsService = statisticsService;
        _templateVariableExtractor = templateVariableExtractor;
        _requestLogger = requestLogger;
        _gatewaySettings = gatewaySettings;
        _apiKeyContext = apiKeyContext;
    }

    public async Task<object> HandleListModelsAsync()
    {
        var presets = await _presetManager.GetAllPresetsAsync();
        var models = _apiKeyContext.FilterAllowed(presets).Select(p => new ModelInfo
        {
            Id = p.Name,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            OwnedBy = "local"
        }).ToList();

        return new { data = models };
    }

    /// <summary>
    /// Extended model listing for clients that need to self-configure without a manual model
    /// setup step (e.g. the Sockless LLM Router VS Code Copilot provider) — context size, output
    /// budget, and modality/tool support per preset, none of which the plain OpenAI
    /// <c>/v1/models</c> shape carries.
    /// </summary>
    public async Task<object> HandleListModelCapabilitiesAsync()
    {
        var presets = await _presetManager.GetAllPresetsAsync();
        var models = _apiKeyContext.FilterAllowed(presets).Select(BuildCapabilities).ToList();

        return new { data = models };
    }

    private ModelCapabilitiesInfo BuildCapabilities(ModelPreset preset)
    {
        // When the preset's server is running, its live /props modalities are the most accurate
        // source for vision/audio support — fall back to preset data otherwise.
        var props = (_serverManager.GetProvider(preset.ServerInstanceId) as IServerCapacityProvider)?.ServerProps;

        // ContextSize (-c) is what the server is launched with — the figure clients should budget
        // against; GgufContextLength is only the model's native maximum. (Not taken from /props:
        // its default_generation_settings.n_ctx is the per-slot slice, i.e. -c divided by -np.)
        var contextLength = preset.ContextSize ?? preset.GgufContextLength ?? 4096;

        // PredictN (-n) is an explicit generation cap when set and positive. Otherwise fall back
        // to a fraction of the context window, since llama.cpp has no fixed output limit of its
        // own and a client still needs a concrete number to budget against.
        var maxOutputTokens = preset.PredictN is > 0
            ? preset.PredictN.Value
            : Math.Max(contextLength / 2, 1024);

        return new ModelCapabilitiesInfo
        {
            Id = preset.Name,
            Name = preset.Name,
            ContextLength = contextLength,
            MaxOutputTokens = maxOutputTokens,
            // Prefer the running server's declared modalities; else a projector must be
            // explicitly configured (file or URL) for this preset to accept image input —
            // MmprojAuto alone doesn't guarantee one exists for the model.
            Vision = props?.Vision ?? (!string.IsNullOrEmpty(preset.Mmproj) || !string.IsNullOrEmpty(preset.MmprojUrl)),
            Audio = props?.Audio ?? false,
            // Tool calling relies on llama.cpp's jinja template rendering; only an explicit
            // Jinja=false rules it out, since null means "use llama.cpp's default".
            ToolCalling = preset.Jinja != false,
            ParameterSize = preset.GgufParameterSize,
            Quantization = preset.GgufQuantizationLevel,
            SupportsReasoningEffort = ReasoningCapabilityDetector.SupportsReasoningEffort(preset.GgufChatTemplate, _templateVariableExtractor),
            ReasoningEffort = preset.ReasoningEffort,
            ReasoningEffortOptions = ReasoningCapabilityDetector.GetReasoningEffortOptions(preset.GgufChatTemplate, _templateVariableExtractor)
        };
    }

    public async Task<IResult> HandleChatCompletionAsync(HttpRequest httpRequest, HttpResponse httpResponse, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(httpRequest.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);
        _logger.LogDebug("Received chat completion request: {Body}", body);
        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(body);
        if (request is null) return Results.BadRequest("Invalid JSON in request body");

        // Reject models this API key isn't scoped to before touching the routing engine —
        // RoutingEngine's round-robin fallback would otherwise happily route an unresolved
        // model name to any healthy server, silently bypassing the scoping.
        var requestedPreset = _presetManager.GetAllPresets().FirstOrDefault(p => p.Name == request.Model);
        if (requestedPreset is not null && !_apiKeyContext.IsModelAllowed(requestedPreset.Id))
        {
            return Microsoft.AspNetCore.Http.Results.Json(new
            {
                error = new { message = $"Model '{request.Model}' is not accessible with this API key.", type = "invalid_request_error" }
            }, statusCode: 403);
        }

        // Build internal RouteRequest from the OpenAI request
        var routeRequest = BuildRouteRequest(request);

        // Log incoming request
        Guid logId = Guid.Empty;
        try { logId = await _requestLogger.LogIncomingAsync(ApiProtocol.OpenAI, "/v1/chat/completions", body, request.Model); }
        catch { /* Logging failure shouldn't block the request */ }

        // Log translated payload
        if (logId != Guid.Empty) { try { await _requestLogger.LogTranslatedPayloadAsync(logId, routeRequest.Payload); } catch { } }

        // Backend-call cancellation: linked to the client's token so a client disconnect
        // aborts the in-flight call to llama.cpp immediately (instead of leaving it to burn
        // GPU time on a response nobody will receive), plus an independent backend timeout so
        // a hung backend still gets cut off even while the client stays connected.
        using var backendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_gatewaySettings.BackendTimeoutSeconds > 0)
        {
            backendCts.CancelAfter(TimeSpan.FromSeconds(_gatewaySettings.BackendTimeoutSeconds));
        }

        // Use the HTTP context token for routing (fast DB query — safe to cancel on client disconnect).
        // RouteAsync itself takes care of starting/restarting the server for the requested model
        // when needed; if it returns null here, that (re)start is running in the background and
        // we queue the request below — same as the Ollama and Claude handlers — instead of
        // failing the request outright.
        // The decision holds a reserved parallel-request slot on the chosen server; `using`
        // releases it when this method returns — after the non-streaming response is built or
        // the streaming loop below completes. A null decision means every candidate server is
        // busy (or still starting), so we queue further down.
        using var decision = await _routingEngine.RouteAsync(routeRequest, cancellationToken);
        var server = decision?.Server;

        // Backend token — cancels on client disconnect or the backend timeout, whichever is first
        var backendToken = backendCts?.Token ?? CancellationToken.None;

        if (server != null)
        {
            if (request.Stream)
            {
                    httpResponse.StatusCode = 200;
                    httpResponse.Headers.ContentType = "text/event-stream";
                    httpResponse.Headers.CacheControl = "no-cache";

                var provider = _serverManager.GetProvider(server.Id);
                if (provider is null)
                    return Microsoft.AspNetCore.Http.Results.Problem($"No backend provider registered for instance {server.Name}", statusCode: 503);

                // Write directly to BodyWriter to bypass HttpResponseStreamWriter's internal buffer
                var bodyWriter = httpResponse.BodyWriter;

                var completionId = $"chatcmpl-{Guid.NewGuid():N}";
                    var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    // Tool-call indexes whose opening delta (id + type + name) has already been
                    // sent to the client. OpenAI's wire format carries id/type/name only on the
                    // first delta of each call; every later delta carries just the arguments
                    // fragment. Restating the name (even as "") on later deltas makes clients that
                    // assign rather than append function.name overwrite the real name with "" —
                    // which is exactly how a working tool call turns into an un-dispatchable one
                    // with the arguments intact but the name blank.
                    var toolCallOpenerSent = new HashSet<int>();

                    // Log the response ID for future OpenAI response_id correlation
                    if (logId != Guid.Empty) { try { await _requestLogger.LogResponseIdAsync(logId, completionId); } catch { } }

                    // Yield first chunk with role
                    var firstChunk = $"data: {JsonSerializer.Serialize(new ChatCompletionChunk
                    {
                        Id = completionId,
                        Created = created,
                        Model = request.Model,
                        Choices = new List<ChunkChoice>
                        {
                            new ChunkChoice
                            {
                                Index = 0,
                                Delta = new DeltaMessage { Role = "assistant", Content = ChatMessageContent.FromText(string.Empty) }
                            }
                        }
                    })}\r\n\r\n";
                    await bodyWriter.WriteAsync(Encoding.UTF8.GetBytes(firstChunk), cancellationToken);
                    await bodyWriter.FlushAsync(cancellationToken);

                    try
                    {
                    // Note: no early "if backendToken.IsCancellationRequested break" guard here —
                    // once cancelled (client disconnect or backend timeout), the provider stops
                    // reading from llama.cpp and yields exactly one more chunk: a synthetic final
                    // chunk carrying whatever partial content/stats it captured. Breaking before
                    // that chunk is processed would discard it and skip stats/logging below.
                    await foreach (var chunk in provider.SendStreamRequestAsync(routeRequest.Payload, routeRequest.Protocol, backendToken))
                    {
                        if (chunk.IsFinal && chunk.Response is not null)
                        {
                            // Final chunk with finish_reason, usage, and timings
                            var timing = BuildTimings(chunk.Response);

                            // llama.cpp reports finish_reason "tool_calls" even when the only
                            // tool call it produced was dropped for having no name (a
                            // speculative/MTP-decoding glitch — see the provider's stream loop).
                            // Report "stop" in that case so the client doesn't wait on a tool
                            // call that never arrived.
                            var finishReason = chunk.Response.FinishReason ?? "stop";
                            if (finishReason == "tool_calls" && chunk.Response.ToolCalls is not { Count: > 0 })
                                finishReason = "stop";
                            var finalChunkText = $"data: {JsonSerializer.Serialize(new ChatCompletionChunk
                            {
                                Id = completionId,
                                Created = created,
                                Model = request.Model,
                                Choices = new List<ChunkChoice>
                                {
                                    new ChunkChoice
                                    {
                                        Index = 0,
                                        // Deliberately empty: the client already received the full
                                        // tool call (name + arguments) via the preceding incremental
                                        // deltas. Restating it here — as this used to do — duplicates
                                        // it for any client that does the standard (and
                                        // OpenAI-recommended) thing of blindly concatenating each
                                        // delta's content/arguments, since this frame's data would
                                        // get appended on top of what was already assembled. Real
                                        // OpenAI/llama.cpp send an empty delta on the terminating
                                        // frame for exactly this reason.
                                        Delta = new DeltaMessage(),
                                        FinishReason = finishReason
                                    }
                                },
                                Usage = new Usage
                                {
                                    PromptTokens = chunk.Response.PromptTokensProcessed,
                                    CompletionTokens = chunk.Response.GeneratedTokenCount,
                                    TotalTokens = chunk.Response.PromptTokensProcessed + chunk.Response.GeneratedTokenCount
                                },
                                Timings = timing
                            })}\r\n\r\n";
                            // Best-effort: the client may already be gone (that's the same
                            // disconnect that just cancelled the backend call above) — don't let
                            // a failed write skip the statistics/logging below.
                            try
                            {
                                await bodyWriter.WriteAsync(Encoding.UTF8.GetBytes(finalChunkText), cancellationToken);
                                await bodyWriter.FlushAsync(cancellationToken);
                            }
                            catch (OperationCanceledException) { }

                            // Record statistics from final response
                            try
                            {
                                var presetId = routeRequest.PresetId ?? server.ActivePresetId;
                                var preset = presetId.HasValue ? _presetManager.GetById(presetId.Value) : null;
                                await _statisticsService.RecordRequestAsync(server, preset, chunk.Response, routeRequest.ApiKeyId);
                            }
                            catch { /* Stats recording failure shouldn't block the response */ }

                            // Log request completion
                            if (logId != Guid.Empty)
                            {
                                try
                                {
                                    var preset = routeRequest.PresetId.HasValue ? _presetManager.GetById(routeRequest.PresetId.Value) : null;
                                    await _requestLogger.LogCompletionAsync(logId, server, preset, chunk.Response, 200,
                                        $"Streamed {chunk.Response.GeneratedTokenCount} tokens", true, false);
                                }
                                catch { /* Logging failure shouldn't block the response */ }
                            }
                        }
                        else if (chunk.ToolCallDeltas is not null)
                        {
                            // Emit tool-call deltas in true OpenAI wire shape: id/type/name only
                            // on the first delta per index, arguments-only on every delta after.
                            var toolCallsOut = new List<object>(chunk.ToolCallDeltas.Count);
                            foreach (var tc in chunk.ToolCallDeltas)
                            {
                                int idx = tc.Index ?? 0;

                                // Carry "name" only when this delta actually has a fragment of it —
                                // never as an empty string, which clients that assign (rather than
                                // append) function.name would use to wipe the real name.
                                object function = string.IsNullOrEmpty(tc.Function.Name)
                                    ? new { arguments = tc.Function.Arguments }
                                    : new { name = tc.Function.Name, arguments = tc.Function.Arguments };

                                if (toolCallOpenerSent.Add(idx))
                                    toolCallsOut.Add(new { index = idx, id = tc.Id, type = "function", function });
                                else
                                    toolCallsOut.Add(new { index = idx, function });
                            }

                            var frame = new
                            {
                                id = completionId,
                                @object = "chat.completion.chunk",
                                created,
                                model = request.Model,
                                choices = new[]
                                {
                                    new { index = 0, delta = new { tool_calls = toolCallsOut }, finish_reason = (string?)null }
                                }
                            };
                            var dataText = $"data: {JsonSerializer.Serialize(frame)}\r\n\r\n";
                            await bodyWriter.WriteAsync(Encoding.UTF8.GetBytes(dataText), cancellationToken);
                        }
                        else if (!string.IsNullOrEmpty(chunk.TextDelta) || !string.IsNullOrEmpty(chunk.ReasoningContentDelta))
                        {
                            var dataText = $"data: {JsonSerializer.Serialize(new ChatCompletionChunk
                            {
                                Id = completionId,
                                Created = created,
                                Model = request.Model,
                                Choices = new List<ChunkChoice>
                                {
                                    new ChunkChoice
                                    {
                                        Index = 0,
                                        Delta = new DeltaMessage
                                        {
                                            Content = !string.IsNullOrEmpty(chunk.TextDelta) ? ChatMessageContent.FromText(chunk.TextDelta) : null,
                                            ReasoningContent = chunk.ReasoningContentDelta
                                        }
                                    }
                                }
                            })}\r\n\r\n";
                            await bodyWriter.WriteAsync(Encoding.UTF8.GetBytes(dataText), cancellationToken);
                        }

                        await bodyWriter.FlushAsync(cancellationToken);
                    }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // The stream already started with a 200 status and SSE headers, so a
                        // mid-stream backend failure (e.g. llama.cpp's context-exceeded error)
                        // can't become a proper HTTP error response — the best we can do is tell
                        // the client what happened via an in-band error frame instead of letting
                        // this propagate to Kestrel's unhandled-exception handler, which just
                        // severs the connection and logs a scary stack trace for what is often a
                        // routine, user-actionable condition.
                        var clientMessage = BackendErrorClassifier.ToClientMessage(ex);
                        _logger.LogWarning(ex, "Streaming chat completion failed for model {Model} on server {Server}", request.Model, server.Name);

                        try
                        {
                            var errorFrame = $"data: {JsonSerializer.Serialize(new
                            {
                                error = new
                                {
                                    message = clientMessage,
                                    type = "invalid_request_error",
                                    code = BackendErrorClassifier.IsContextExceeded(ex) ? "context_length_exceeded" : null
                                }
                            })}\r\n\r\n";
                            await bodyWriter.WriteAsync(Encoding.UTF8.GetBytes(errorFrame), cancellationToken);
                            await bodyWriter.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\r\n\r\n"), cancellationToken);
                            await bodyWriter.FlushAsync(cancellationToken);
                        }
                        catch (OperationCanceledException) { }

                        if (logId != Guid.Empty) { try { await _requestLogger.LogErrorAsync(logId, clientMessage); } catch { } }

                        return Results.Empty;
                    }

                    // Signal end of stream (best-effort — the client may already be gone)
                    try
                    {
                        await bodyWriter.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\r\n\r\n"), cancellationToken);
                        await bodyWriter.FlushAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) { }

                return Results.Empty;
            }

            try
            {
                var result = await ProcessOnServer(server, request, routeRequest, logId, backendToken);
                return Microsoft.AspNetCore.Http.Results.Json(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var clientMessage = BackendErrorClassifier.ToClientMessage(ex);
                _logger.LogWarning(ex, "Non-streaming chat completion failed for model {Model} on server {Server}", request.Model, server.Name);
                if (logId != Guid.Empty) { try { await _requestLogger.LogErrorAsync(logId, clientMessage); } catch { } }

                return Microsoft.AspNetCore.Http.Results.Json(new
                {
                    error = new
                    {
                        message = clientMessage,
                        type = "invalid_request_error",
                        code = BackendErrorClassifier.IsContextExceeded(ex) ? "context_length_exceeded" : null
                    }
                }, statusCode: BackendErrorClassifier.IsContextExceeded(ex) ? 400 : 502);
            }
        }

        // Queue the request — use backend token so a client disconnect doesn't abort the queue wait
        var response = await _queue.EnqueueAsync(routeRequest, backendToken);

        // Log queued request completion
        if (logId != Guid.Empty)
        {
            try
            {
                var preset = routeRequest.PresetId.HasValue ? _presetManager.GetById(routeRequest.PresetId.Value) : null;
                await _requestLogger.LogCompletionAsync(logId, null, preset, response, 200,
                    $"Queued: {response.GeneratedTokenCount} tokens", false, true);
            }
            catch { /* Logging failure shouldn't block the response */ }
        }

        // Convert RouteResponse back to OpenAI format
        return Microsoft.AspNetCore.Http.Results.Json(BuildCompletionResponse(request.Model, response));
    }

    private async Task<object> ProcessOnServer(
        ServerInstance server,
        ChatCompletionRequest chatRequest,
        RouteRequest routeRequest,
        Guid logId,
        CancellationToken cancellationToken)
    {
        var provider = _serverManager.GetProvider(server.Id);
        if (provider is null)
            throw new InvalidOperationException($"No backend provider registered for instance {server.Name}");

        // Send request to the backend
        var response = await provider.SendRequestAsync(routeRequest.Payload, routeRequest.Protocol, cancellationToken);
        if (response == null)
            throw new InvalidOperationException($"Backend returned no response from server {server.Name}");

        // Record statistics
        try
        {
            var presetId = routeRequest.PresetId ?? server.ActivePresetId;
            var preset = presetId.HasValue ? _presetManager.GetById(presetId.Value) : null;
            await _statisticsService.RecordRequestAsync(server, preset, response, routeRequest.ApiKeyId);
        }
        catch { /* Stats recording failure shouldn't block the response */ }

        // Log request completion
        if (logId != Guid.Empty)
        {
            try
            {
                var preset = routeRequest.PresetId.HasValue ? _presetManager.GetById(routeRequest.PresetId.Value) : null;
                await _requestLogger.LogCompletionAsync(logId, server, preset, response, 200,
                    $"Non-streaming: {response.GeneratedTokenCount} tokens", false, false);
            }
            catch { /* Logging failure shouldn't block the response */ }
        }

        return BuildCompletionResponse(chatRequest.Model, response);
    }

    private LlamaCppTimings? BuildTimings(RouteResponse response)
    {
        // Prefer the rich timings object from the backend if available
        if (response.BackendTimings != null)
            return response.BackendTimings;

        // Fallback: construct from scalar properties on RouteResponse
        var timing = new LlamaCppTimings
        {
            PromptN = response.PromptTokensProcessed,
            PromptMs = response.PromptProcessingMs > 0 ? response.PromptProcessingMs : null,
            GenerationN = response.GeneratedTokenCount,
            GenerationMs = response.GenerationMs > 0 ? response.GenerationMs : null
        };

        // Calculate per-token and throughput metrics for prompt phase
        if (timing.PromptMs.HasValue && timing.PromptN > 0)
        {
            timing.PromptPerTokenMs = timing.PromptMs.Value / timing.PromptN;
            timing.PromptPerSecond = timing.PromptN / (timing.PromptMs.Value / 1000.0);
        }

        // Calculate per-token and throughput metrics for generation phase
        if (timing.GenerationMs.HasValue && timing.GenerationN > 0)
        {
            timing.GenerationPerTokenMs = timing.GenerationMs.Value / timing.GenerationN;
            timing.GenerationPerSecond = timing.GenerationN / (timing.GenerationMs.Value / 1000.0);
        }

        // Speculative decoding metrics
        if (response.DraftAccepted > 0)
        {
            timing.PredictedN = response.DraftAccepted;
        }

        return timing;
    }

    private RouteRequest BuildRouteRequest(ChatCompletionRequest request)
    {
        // Find preset matching the model name
        var presets = _presetManager.GetAllPresets();
        var preset = presets.FirstOrDefault(p => p.Name == request.Model);

        // llama.cpp (like OpenAI) only includes token usage in the SSE stream when
        // stream_options.include_usage is set. Force it on so usage/stats are always
        // available, regardless of whether the calling client requested it.
        if (request.Stream)
        {
            request.StreamOptions ??= new StreamOptions();
            request.StreamOptions.IncludeUsage = true;
        }

        SanitizeMessages(request.Messages);

        var payload = JsonSerializer.Serialize(request, BackendJsonOpts);
        _logger.LogDebug("RouteRequest payload for {Model}: {Payload}", request.Model, payload);

        return new RouteRequest
        {
            ModelName = request.Model,
            PresetId = preset?.Id,
            ApiKeyId = _apiKeyContext.CurrentKey?.Id,
            Payload = payload
        };
    }

    /// <summary>
    /// Guards against forwarding messages that llama.cpp's chat-completions endpoint rejects
    /// outright. Request messages are forwarded to the backend essentially as-is, so a client
    /// replaying a stored conversation that picked up a malformed turn (e.g. from a prior router
    /// bug, or from any other source) would otherwise 400 on every subsequent request until that
    /// turn ages out of its history. Rather than reject the whole request, patch each message
    /// into a shape the backend accepts:
    /// <list type="bullet">
    /// <item>Assistant messages need either non-empty content or tool_calls ("Assistant message
    /// must contain either 'content' or 'tool_calls'!") — an assistant turn with neither gets
    /// explicit empty content so it reads as a no-op turn.</item>
    /// <item>Every non-assistant message (system, user, tool, ...) must carry a 'content' field
    /// ("All non-assistant messages must contain 'content'"). A null/omitted content — which is
    /// how the serializer emits <see cref="ChatMessage.Content"/> when it's null — is replaced
    /// with an empty string.</item>
    /// </list>
    /// </summary>
    private static void SanitizeMessages(List<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            bool hasContent = message.Content is { Text.Length: > 0 } or { Parts.Count: > 0 };

            if (message.Role == "assistant")
            {
                bool hasToolCalls = message.ToolCalls is { Count: > 0 };
                if (hasContent || hasToolCalls) continue;

                message.Content = ChatMessageContent.FromText(string.Empty);
                message.ToolCalls = null;
                continue;
            }

            // Non-assistant: llama.cpp requires a present 'content' field. An empty-string
            // content object still serializes as "content":"", which satisfies it; a null
            // Content is dropped entirely by the null-ignoring serializer and triggers the 400.
            message.Content ??= ChatMessageContent.FromText(string.Empty);
        }
    }

    private ChatCompletionResponse BuildCompletionResponse(string model, RouteResponse response)
    {
        // Treat a non-null-but-empty ToolCalls list the same as null: an empty list here would
        // otherwise omit "content" (below) in favor of an empty "tool_calls" array, producing an
        // assistant message with neither — which llama.cpp rejects on the very next turn once
        // the client stores this response and replays it as history.
        var toolCalls = response.ToolCalls is { Count: > 0 } ? response.ToolCalls : null;

        return new ChatCompletionResponse
        {
            Id = $"chatcmpl-{Guid.NewGuid():N}",
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = model,
            Choices = new List<Choice>
            {
                new Choice
                {
                    Index = 0,
                    Message = new ChatMessage
                    {
                        Role = "assistant",
                        Content = toolCalls is null ? ChatMessageContent.FromText(response.Payload) : null,
                        ToolCalls = toolCalls,
                        ReasoningContent = response.ReasoningContent
                    },
                    FinishReason = response.FinishReason ?? "stop"
                }
            },
            Usage = new Usage
            {
                PromptTokens = response.PromptTokensProcessed,
                CompletionTokens = response.GeneratedTokenCount,
                TotalTokens = response.PromptTokensProcessed + response.GeneratedTokenCount
            }
        };
    }
}
