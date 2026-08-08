using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

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
    private readonly IServerManager _serverManager;
    private readonly IPresetManager _presetManager;
    private readonly IRoutingEngine _routingEngine;
    private readonly IRequestQueueService _queue;
    private readonly IStatisticsService _statisticsService;
    private readonly IApiRequestLogger _requestLogger;
    private readonly GatewaySettings _gatewaySettings;

    public ApiProtocol Protocol => ApiProtocol.Claude;
    public string PathPrefix => "/v1";

    public ClaudeHandler(
        IServerManager serverManager,
        IPresetManager presetManager,
        IRoutingEngine routingEngine,
        IRequestQueueService queue,
        IStatisticsService statisticsService,
        IApiRequestLogger requestLogger,
        GatewaySettings gatewaySettings)
    {
        _serverManager = serverManager;
        _presetManager = presetManager;
        _routingEngine = routingEngine;
        _queue = queue;
        _statisticsService = statisticsService;
        _requestLogger = requestLogger;
        _gatewaySettings = gatewaySettings;
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
        var request = JsonSerializer.Deserialize<CreateMessageRequest>(body);
        if (request is null) return Microsoft.AspNetCore.Http.Results.BadRequest("Invalid JSON in request body");

        // Build internal RouteRequest from the Claude request
        var routeRequest = BuildRouteRequest(request);

        // Log incoming request
        Guid logId = Guid.Empty;
        try { logId = await _requestLogger.LogIncomingAsync(ApiProtocol.Claude, "/v1/messages", body, request.Model); }
        catch { /* Logging failure shouldn't block the request */ }

        // Log translated payload
        if (logId != Guid.Empty) { try { await _requestLogger.LogTranslatedPayloadAsync(logId, routeRequest.Payload); } catch { } }

        // Create a backend-specific cancellation token that is NOT tied to client disconnection.
        using var backendCts = new CancellationTokenSource();
        if (_gatewaySettings.BackendTimeoutSeconds > 0)
        {
            backendCts.CancelAfter(TimeSpan.FromSeconds(_gatewaySettings.BackendTimeoutSeconds));
        }

        // Use the HTTP context token for routing (fast DB query — safe to cancel on client disconnect)
        var server = await _routingEngine.RouteAsync(routeRequest, cancellationToken);

        // Backend token — survives client disconnect but respects the backend timeout
        var backendToken = backendCts.Token;

        if (server != null)
        {
            if (request.Stream)
            {
                    httpResponse.StatusCode = 200;

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

                    // content_block_start event
                    await httpResponse.WriteAsync($"event: content_block_start\ndata: {JsonSerializer.Serialize(new ContentBlockStartData
                    {
                        Type = "content_block_start",
                        Index = 0,
                        ContentBlock = new ContentBlock { Type = "text", Text = string.Empty }
                    })}\r\n\r\n", cancellationToken);
                    await httpResponse.Body.FlushAsync(cancellationToken);

                    await foreach (var chunk in provider.SendStreamRequestAsync(routeRequest.Payload, backendToken))
                    {
                        if (backendToken.IsCancellationRequested) break;

                        if (chunk.IsFinal && chunk.Response is not null)
                        {
                            // message_delta event with stop reason
                            await httpResponse.WriteAsync($"event: message_delta\ndata: {JsonSerializer.Serialize(new MessageDeltaData
                            {
                                Type = "message_delta",
                                Delta = new DeltaMessageDelta
                                {
                                    StopReason = "end_turn",
                                    StopSequence = null
                                }
                            })}\r\n\r\n", cancellationToken);

                            // message_stop event
                            await httpResponse.WriteAsync($"event: message_stop\ndata: {JsonSerializer.Serialize(new MessageStopData
                            {
                                Type = "message_stop"
                            })}\r\n\r\n", cancellationToken);

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
                        else if (!string.IsNullOrEmpty(chunk.TextDelta))
                        {
                            // content_block_delta event
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

                        await httpResponse.Body.FlushAsync(cancellationToken);
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
        var response = await provider.SendRequestAsync(routeRequest.Payload, cancellationToken);
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
            // Omit null fields — backend rejects "name":null etc.
            Payload = JsonSerializer.Serialize(request, new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull })
        };
    }

    private CreateMessageResponse BuildMessageResponse(string model, RouteResponse response)
    {
        return new CreateMessageResponse
        {
            Id = $"msg_{Guid.NewGuid():N}",
            Model = model,
            Content = new List<ContentBlock>
            {
                new ContentBlock { Type = "text", Text = response.Payload }
            },
            StopReason = "end_turn",
            Usage = new LR.Core.Models.Claude.Usage
            {
                InputTokens = response.PromptTokensProcessed,
                OutputTokens = response.GeneratedTokenCount
            }
        };
    }
}
