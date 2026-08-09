using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Models.OpenAI;

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
    private readonly IApiRequestLogger _requestLogger;
    private readonly GatewaySettings _gatewaySettings;

    public ApiProtocol Protocol => ApiProtocol.OpenAI;
    public string PathPrefix => "/v1";

    public OpenAiHandler(
        ILogger<OpenAiHandler> logger,
        IServerManager serverManager,
        IPresetManager presetManager,
        IRoutingEngine routingEngine,
        IRequestQueueService queue,
        IStatisticsService statisticsService,
        IApiRequestLogger requestLogger,
        GatewaySettings gatewaySettings)
    {
        _logger = logger;
        _serverManager = serverManager;
        _presetManager = presetManager;
        _routingEngine = routingEngine;
        _queue = queue;
        _statisticsService = statisticsService;
        _requestLogger = requestLogger;
        _gatewaySettings = gatewaySettings;
    }

    public async Task<object> HandleListModelsAsync()
    {
        var presets = await _presetManager.GetAllPresetsAsync();
        var models = presets.Select(p => new ModelInfo
        {
            Id = p.Name,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            OwnedBy = "local"
        }).ToList();

        return new { data = models };
    }

    public async Task<IResult> HandleChatCompletionAsync(HttpRequest httpRequest, HttpResponse httpResponse, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(httpRequest.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);
        _logger.LogDebug("Received chat completion request: {Body}", body);
        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(body);
        if (request is null) return Results.BadRequest("Invalid JSON in request body");

        // Build internal RouteRequest from the OpenAI request
        var routeRequest = BuildRouteRequest(request);

        // Log incoming request
        Guid logId = Guid.Empty;
        try { logId = await _requestLogger.LogIncomingAsync(ApiProtocol.OpenAI, "/v1/chat/completions", body, request.Model); }
        catch { /* Logging failure shouldn't block the request */ }

        // Log translated payload
        if (logId != Guid.Empty) { try { await _requestLogger.LogTranslatedPayloadAsync(logId, routeRequest.Payload); } catch { } }

        // Create a backend-specific cancellation token that is NOT tied to client disconnection.
        // This prevents the backend call from being cancelled when the client disconnects or times out,
        // which was causing lost connections and missing backend response logs.
        using var backendCts = new CancellationTokenSource();
        if (_gatewaySettings.BackendTimeoutSeconds > 0)
        {
            backendCts.CancelAfter(TimeSpan.FromSeconds(_gatewaySettings.BackendTimeoutSeconds));
        }

        // Use the HTTP context token for routing (fast DB query — safe to cancel on client disconnect).
        // RouteAsync itself takes care of starting/restarting the server for the requested model
        // when needed; if it returns null here, that (re)start is running in the background and
        // we queue the request below — same as the Ollama and Claude handlers — instead of
        // failing the request outright.
        var server = await _routingEngine.RouteAsync(routeRequest, cancellationToken);

        // Backend token — survives client disconnect but respects the backend timeout
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

                    await foreach (var chunk in provider.SendStreamRequestAsync(routeRequest.Payload, backendToken))
                    {
                        if (backendToken.IsCancellationRequested) break;

                        if (chunk.IsFinal && chunk.Response is not null)
                        {
                            // Final chunk with finish_reason, usage, and timings
                            var timing = BuildTimings(chunk.Response);
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
                                        Delta = new DeltaMessage { ToolCalls = chunk.Response.ToolCalls },
                                        FinishReason = chunk.Response.FinishReason ?? "stop"
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
                            await bodyWriter.WriteAsync(Encoding.UTF8.GetBytes(finalChunkText), cancellationToken);
                            await bodyWriter.FlushAsync(cancellationToken);

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
                        else if (!string.IsNullOrEmpty(chunk.TextDelta) || !string.IsNullOrEmpty(chunk.ReasoningContentDelta) || chunk.ToolCallDeltas is not null)
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
                                            ReasoningContent = chunk.ReasoningContentDelta,
                                            ToolCalls = chunk.ToolCallDeltas
                                        }
                                    }
                                }
                            })}\r\n\r\n";
                            await bodyWriter.WriteAsync(Encoding.UTF8.GetBytes(dataText), cancellationToken);
                        }

                        await bodyWriter.FlushAsync(cancellationToken);
                    }

                    // Signal end of stream
                    await bodyWriter.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\r\n\r\n"), cancellationToken);
                    await bodyWriter.FlushAsync(cancellationToken);

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

        var payload = JsonSerializer.Serialize(request, BackendJsonOpts);
        _logger.LogDebug("RouteRequest payload for {Model}: {Payload}", request.Model, payload);

        return new RouteRequest
        {
            ModelName = request.Model,
            PresetId = preset?.Id,
            Payload = payload
        };
    }

    private ChatCompletionResponse BuildCompletionResponse(string model, RouteResponse response)
    {
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
                        Content = response.ToolCalls is null ? ChatMessageContent.FromText(response.Payload) : null,
                        ToolCalls = response.ToolCalls,
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
