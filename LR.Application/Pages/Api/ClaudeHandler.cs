using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Models.Claude;

namespace LR.Application.Pages.Api;

/// <summary>
/// Claude Messages API handler.
/// Maps /v1/messages endpoint.
/// </summary>
public class ClaudeHandler : IProtocolHandler
{
    /// <summary>
    /// The placeholder "input" Anthropic sends on a tool_use content_block_start before any
    /// input_json_delta fragments arrive — the real arguments are assembled from those deltas.
    /// </summary>
    private static readonly JsonElement EmptyJsonObject = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly ILogger<ClaudeHandler> _logger;
    private readonly IServerManager _serverManager;
    private readonly IPresetManager _presetManager;
    private readonly IRoutingEngine _routingEngine;
    private readonly IRequestQueueService _queue;
    private readonly IStatisticsService _statisticsService;
    private readonly IApiRequestLogger _requestLogger;
    private readonly GatewaySettings _gatewaySettings;
    private readonly IApiKeyRequestContext _apiKeyContext;

    public ApiProtocol Protocol => ApiProtocol.Claude;
    public string PathPrefix => "/v1";

    public ClaudeHandler(
        ILogger<ClaudeHandler> logger,
        IServerManager serverManager,
        IPresetManager presetManager,
        IRoutingEngine routingEngine,
        IRequestQueueService queue,
        IStatisticsService statisticsService,
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
        _requestLogger = requestLogger;
        _gatewaySettings = gatewaySettings;
        _apiKeyContext = apiKeyContext;
    }

    public Task<object> HandleListModelsAsync()
    {
        // Claude doesn't have a separate models endpoint - model info is returned in message responses.
        return Task.FromResult<object>(new { });
    }

    public async Task<IResult> HandleChatCompletionAsync(HttpRequest httpRequest, HttpResponse httpResponse, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(httpRequest.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);
        CreateMessageRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<CreateMessageRequest>(body);
        }
        catch (JsonException ex)
        {
            return Microsoft.AspNetCore.Http.Results.Json(new
            {
                type = "error",
                error = new { type = "invalid_request_error", message = $"Invalid JSON in request body: {ex.Message}" }
            }, statusCode: 400);
        }
        if (request is null) return Microsoft.AspNetCore.Http.Results.BadRequest("Invalid JSON in request body");

        // Reject models this API key isn't scoped to before touching the routing engine —
        // RoutingEngine's round-robin fallback would otherwise happily route an unresolved
        // model name to any healthy server, silently bypassing the scoping.
        var requestedPreset = _presetManager.GetAllPresets().FirstOrDefault(p => p.Name == request.Model);
        if (requestedPreset is not null && !_apiKeyContext.IsModelAllowed(requestedPreset.Id))
        {
            return Microsoft.AspNetCore.Http.Results.Json(new
            {
                type = "error",
                error = new { type = "permission_error", message = $"Model '{request.Model}' is not accessible with this API key." }
            }, statusCode: 403);
        }

        // Build internal RouteRequest from the Claude request
        var routeRequest = BuildRouteRequest(request);

        // Log incoming request
        Guid logId = Guid.Empty;
        try { logId = await _requestLogger.LogIncomingAsync(ApiProtocol.Claude, "/v1/messages", body, request.Model); }
        catch { /* Logging failure shouldn't block the request */ }

        // Log translated payload
        if (logId != Guid.Empty) { try { await _requestLogger.LogTranslatedPayloadAsync(logId, routeRequest.Payload); } catch { } }

        // Backend-call cancellation: linked to the client's token so a client disconnect
        // aborts the in-flight call to llama.cpp immediately, plus an independent backend
        // timeout so a hung backend still gets cut off even while the client stays connected.
        using var backendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_gatewaySettings.BackendTimeoutSeconds > 0)
        {
            backendCts.CancelAfter(TimeSpan.FromSeconds(_gatewaySettings.BackendTimeoutSeconds));
        }

        // Use the HTTP context token for routing (fast DB query — safe to cancel on client disconnect)
        var server = await _routingEngine.RouteAsync(routeRequest, cancellationToken);

        // Backend token — cancels on client disconnect or the backend timeout, whichever is first
        var backendToken = backendCts.Token;

        if (server != null)
        {
            if (request.Stream)
            {
                    httpResponse.StatusCode = 200;
                    httpResponse.Headers.ContentType = "text/event-stream";
                    httpResponse.Headers.CacheControl = "no-cache";
                    // Disable Kestrel response buffering so writes go directly to the socket
                    httpResponse.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

                var provider = _serverManager.GetProvider(server.Id);
                if (provider is null)
                    return Microsoft.AspNetCore.Http.Results.Problem($"No backend provider registered for instance {server.Name}", statusCode: 503);

                var messageId = $"msg_{Guid.NewGuid():N}";

                    // message_start event
                    await httpResponse.WriteAsync($"event: message_start\ndata: {JsonSerializer.Serialize(new MessageStartData
                    {
                        Type = "message_start",
                        Message = new MessageEnvelope
                        {
                            Id = messageId,
                            Type = "message",
                            Role = "assistant",
                            Content = new List<object>(),
                            Model = request.Model
                        }
})}\r\n\r\n", cancellationToken);
                    await httpResponse.Body.FlushAsync(cancellationToken);

                    // content_block_start event — block 0 is reserved for text; any tool_use blocks
                    // the model emits get their own client-facing index (see toolBlockClientIndex
                    // below), independent of whatever index llama.cpp assigns internally.
                    await httpResponse.WriteAsync($"event: content_block_start\ndata: {JsonSerializer.Serialize(new ContentBlockStartData
                    {
                        Type = "content_block_start",
                        Index = 0,
                        ContentBlock = new ContentBlock { Type = "text", Text = string.Empty }
                    })}\r\n\r\n", cancellationToken);
                    await httpResponse.Body.FlushAsync(cancellationToken);

                    // Maps the backend's tool-call block index (RouteStreamChunk.ToolCallDeltas[].Index)
                    // to the index this client sees, since client-facing index 0 is already taken by
                    // the eagerly-opened text block above.
                    var toolBlockClientIndex = new Dictionary<int, int>();
                    int nextToolClientIndex = 1;

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
                            // Best-effort: the client may already be gone (that's the same
                            // disconnect that just cancelled the backend call above) — don't let
                            // a failed write skip the statistics/logging below.
                            try
                            {
                                // content_block_stop for every block opened above (text + any tool_use
                                // blocks). Required before message_delta/message_stop for a well-formed
                                // event sequence; strict clients (e.g. the official Anthropic SDK) reject
                                // a stream missing it.
                                foreach (var openIndex in new[] { 0 }.Concat(toolBlockClientIndex.Values).OrderBy(i => i))
                                {
                                    await httpResponse.WriteAsync($"event: content_block_stop\ndata: {JsonSerializer.Serialize(new ContentBlockStopData
                                    {
                                        Type = "content_block_stop",
                                        Index = openIndex
                                    })}\r\n\r\n", cancellationToken);
                                }
                                await httpResponse.Body.FlushAsync(cancellationToken);

                                // message_delta event with stop reason and final usage
                                await httpResponse.WriteAsync($"event: message_delta\ndata: {JsonSerializer.Serialize(new MessageDeltaData
                                {
                                    Type = "message_delta",
                                    Delta = new DeltaMessageDelta
                                    {
                                        StopReason = chunk.Response.FinishReason ?? "end_turn",
                                        StopSequence = null
                                    },
                                    Usage = new Usage
                                    {
                                        InputTokens = chunk.Response.PromptTokensProcessed,
                                        OutputTokens = chunk.Response.GeneratedTokenCount
                                    }
                                })}\r\n\r\n", cancellationToken);

                                // message_stop event
                                await httpResponse.WriteAsync($"event: message_stop\ndata: {JsonSerializer.Serialize(new MessageStopData
                                {
                                    Type = "message_stop"
                                })}\r\n\r\n", cancellationToken);
                            }
                            catch (OperationCanceledException) { }

                            // Record statistics from final response
                            try
                            {
                                var presetId = routeRequest.PresetId ?? server.ActivePresetId;
                                var preset = presetId.HasValue ? _presetManager.GetById(presetId.Value) : null;
                                await _statisticsService.RecordRequestAsync(server, preset, chunk.Response);
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
                        else
                        {
                            // Send thinking_delta event for reasoning content first (Claude protocol)
                            if (!string.IsNullOrEmpty(chunk.ReasoningContentDelta))
                            {
                                await httpResponse.WriteAsync($"event: content_block_delta\ndata: {JsonSerializer.Serialize(new ContentBlockDeltaData
                                {
                                    Type = "content_block_delta",
                                    Index = 0,
                                    Delta = new DeltaContentBlockDelta
                                    {
                                        Type = "thinking_delta",
                                        Thinking = chunk.ReasoningContentDelta
                                    }
                                })}\r\n\r\n", cancellationToken);
                            }

                            // Send text_delta event for regular content
                            if (!string.IsNullOrEmpty(chunk.TextDelta))
                            {
                                await httpResponse.WriteAsync($"event: content_block_delta\ndata: {JsonSerializer.Serialize(new ContentBlockDeltaData
                                {
                                    Type = "content_block_delta",
                                    Index = 0,
                                    Delta = new DeltaContentBlockDelta
                                    {
                                        Type = "text_delta",
                                        Text = chunk.TextDelta
                                    }
                                })}\r\n\r\n", cancellationToken);
                            }

                            // Tool call fragments — each backend block index maps to its own
                            // client-facing content block (index 0 is reserved for text).
                            if (chunk.ToolCallDeltas is not null)
                            {
                                foreach (var toolDelta in chunk.ToolCallDeltas)
                                {
                                    int backendIndex = toolDelta.Index ?? 0;
                                    if (!toolBlockClientIndex.TryGetValue(backendIndex, out int clientIndex))
                                    {
                                        clientIndex = nextToolClientIndex++;
                                        toolBlockClientIndex[backendIndex] = clientIndex;

                                        await httpResponse.WriteAsync($"event: content_block_start\ndata: {JsonSerializer.Serialize(new ContentBlockStartData
                                        {
                                            Type = "content_block_start",
                                            Index = clientIndex,
                                            ContentBlock = new ContentBlock
                                            {
                                                Type = "tool_use",
                                                Text = null,
                                                Id = toolDelta.Id,
                                                Name = toolDelta.Function.Name,
                                                Input = EmptyJsonObject
                                            }
                                        })}\r\n\r\n", cancellationToken);
                                    }

                                    if (!string.IsNullOrEmpty(toolDelta.Function.Arguments))
                                    {
                                        await httpResponse.WriteAsync($"event: content_block_delta\ndata: {JsonSerializer.Serialize(new ContentBlockDeltaData
                                        {
                                            Type = "content_block_delta",
                                            Index = clientIndex,
                                            Delta = new DeltaContentBlockDelta
                                            {
                                                Type = "input_json_delta",
                                                PartialJson = toolDelta.Function.Arguments
                                            }
                                        })}\r\n\r\n", cancellationToken);
                                    }
                                }
                            }
                        }

                        await httpResponse.Body.FlushAsync(cancellationToken);
                    }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Claude streaming response failed for model {Model} on server {Server}", request.Model, server.Name);
                        if (logId != Guid.Empty)
                        {
                            try { await _requestLogger.LogErrorAsync(logId, ex.Message); }
                            catch { /* Logging failure shouldn't block the response */ }
                        }
                        throw;
                    }

                return Results.Empty;
            }

            var result = await ProcessOnServer(server, request, routeRequest, logId, backendToken);
            return Microsoft.AspNetCore.Http.Results.Json(result);
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

        // Convert RouteResponse back to Claude format
        return Microsoft.AspNetCore.Http.Results.Json(BuildMessageResponse(request.Model, response));
    }

    private async Task<object> ProcessOnServer(
        ServerInstance server,
        CreateMessageRequest chatRequest,
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
            await _statisticsService.RecordRequestAsync(server, preset, response);
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

        return BuildMessageResponse(chatRequest.Model, response);
    }

    private RouteRequest BuildRouteRequest(CreateMessageRequest request)
    {
        // Find preset matching the model name
        var presets = _presetManager.GetAllPresets();
        var preset = presets.FirstOrDefault(p => p.Name == request.Model);

        return new RouteRequest
        {
            ModelName = request.Model,
            PresetId = preset?.Id,
            Protocol = ApiProtocol.Claude,
            // Omit null fields — backend rejects "name":null etc.
            Payload = JsonSerializer.Serialize(request, new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull })
        };
    }

    private CreateMessageResponse BuildMessageResponse(string model, RouteResponse response)
    {
        var content = new List<ContentBlock>();

        if (!string.IsNullOrEmpty(response.Payload))
            content.Add(new ContentBlock { Type = "text", Text = response.Payload });

        if (response.ToolCalls is { Count: > 0 })
        {
            foreach (var toolCall in response.ToolCalls)
            {
                JsonElement input;
                try
                {
                    input = JsonDocument.Parse(string.IsNullOrEmpty(toolCall.Function.Arguments) ? "{}" : toolCall.Function.Arguments).RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Tool call '{ToolName}' had non-JSON arguments; forwarding an empty object instead: {Arguments}",
                        toolCall.Function.Name, toolCall.Function.Arguments);
                    input = JsonDocument.Parse("{}").RootElement.Clone();
                }

                content.Add(new ContentBlock
                {
                    Type = "tool_use",
                    Text = null,
                    Id = toolCall.Id,
                    Name = toolCall.Function.Name,
                    Input = input
                });
            }
        }

        // Anthropic requires at least one content block.
        if (content.Count == 0)
            content.Add(new ContentBlock { Type = "text", Text = string.Empty });

        return new CreateMessageResponse
        {
            Id = $"msg_{Guid.NewGuid():N}",
            Model = model,
            Content = content,
            StopReason = response.FinishReason ?? "end_turn",
            Usage = new Usage
            {
                InputTokens = response.PromptTokensProcessed,
                OutputTokens = response.GeneratedTokenCount
            }
        };
    }
}
