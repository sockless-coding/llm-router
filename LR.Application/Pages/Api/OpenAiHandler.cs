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

    public ApiProtocol Protocol => ApiProtocol.OpenAI;
    public string PathPrefix => "/v1";

    public OpenAiHandler(
        ILogger<OpenAiHandler> logger,
        IServerManager serverManager,
        IPresetManager presetManager,
        IRoutingEngine routingEngine,
        IRequestQueueService queue,
        IStatisticsService statisticsService)
    {
        _logger = logger;
        _serverManager = serverManager;
        _presetManager = presetManager;
        _routingEngine = routingEngine;
        _queue = queue;
        _statisticsService = statisticsService;
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
        _logger.LogInformation("Received chat completion request: {Body}", body);
        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(body);
        if (request is null) return Results.BadRequest("Invalid JSON in request body");

        // Build internal RouteRequest from the OpenAI request
        var routeRequest = BuildRouteRequest(request);

        // Try to find a server immediately
        var server = await _routingEngine.RouteAsync(routeRequest, cancellationToken);
        if (server is null)
        {
            // No available servers — check if any are running at all for a better error message
            var instances = await _serverManager.GetAllInstancesAsync();
            bool anyRunning = instances.Any(s => s.Status == Core.Models.ServerStatus.Running);
            if (!anyRunning)
                return Results.Problem("No inference servers are currently running. Start a server before sending requests.", statusCode: 503);
            return Results.Problem("All inference servers are busy or unhealthy. Try again later.", statusCode: 503);
        }

        if (server != null)
        {
            if (request.Stream)
            {
                    httpResponse.StatusCode = 200;

                var provider = _serverManager.GetProvider(server.Id);
                if (provider is null)
                    return Microsoft.AspNetCore.Http.Results.Problem($"No backend provider registered for instance {server.Name}", statusCode: 503);

                var completionId = $"chatcmpl-{Guid.NewGuid():N}";
                    var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    // Yield first chunk with role
                    await httpResponse.WriteAsync($"data: {JsonSerializer.Serialize(new ChatCompletionChunk
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
                    })}\r\n\r\n", cancellationToken);
                    await httpResponse.Body.FlushAsync(cancellationToken);

                    await foreach (var chunk in provider.SendStreamRequestAsync(routeRequest.Payload, cancellationToken))
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        if (chunk.IsFinal && chunk.Response is not null)
                        {
                            // Final chunk with finish_reason and timings
                            var timing = BuildTimings(chunk.Response);
                            await httpResponse.WriteAsync($"data: {JsonSerializer.Serialize(new ChatCompletionChunk
                            {
                                Id = completionId,
                                Created = created,
                                Model = request.Model,
                                Choices = new List<ChunkChoice>
                                {
                                    new ChunkChoice
                                    {
                                        Index = 0,
                                        Delta = new DeltaMessage(),
                                        FinishReason = "stop"
                                    }
                                },
                                Timings = timing
                            })}\r\n\r\n", cancellationToken);

                            // Record statistics from final response
                            try
                            {
                                var presetId = routeRequest.PresetId ?? server.ActivePresetId;
                                var preset = presetId.HasValue ? _presetManager.GetById(presetId.Value) : null;
                                await _statisticsService.RecordRequestAsync(server, preset, chunk.Response);
                            }
                            catch { /* Stats recording failure shouldn't block the response */ }
                        }
                        else if (!string.IsNullOrEmpty(chunk.TextDelta))
                        {
                            await httpResponse.WriteAsync($"data: {JsonSerializer.Serialize(new ChatCompletionChunk
                            {
                                Id = completionId,
                                Created = created,
                                Model = request.Model,
                                Choices = new List<ChunkChoice>
                                {
                                    new ChunkChoice
                                    {
                                        Index = 0,
                                        Delta = new DeltaMessage { Content = ChatMessageContent.FromText(chunk.TextDelta) }
                                    }
                                }
                            })}\r\n\r\n", cancellationToken);
                        }

                        await httpResponse.Body.FlushAsync(cancellationToken);
                    }

                    // Signal end of stream
                    await httpResponse.WriteAsync("data: [DONE]\r\n\r\n", cancellationToken);
                    await httpResponse.Body.FlushAsync(cancellationToken);

                return Results.Empty;
            }

            var result = await ProcessOnServer(server, request, routeRequest, cancellationToken);
            return Microsoft.AspNetCore.Http.Results.Json(result);
        }

        // Queue the request
        var response = await _queue.EnqueueAsync(routeRequest, cancellationToken);

        // Convert RouteResponse back to OpenAI format
        return Microsoft.AspNetCore.Http.Results.Json(BuildCompletionResponse(request.Model, response));
    }

    private async Task<object> ProcessOnServer(
        ServerInstance server,
        ChatCompletionRequest chatRequest,
        RouteRequest routeRequest,
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
                    Message = new ChatMessage { Role = "assistant", Content = ChatMessageContent.FromText(response.Payload) },
                    FinishReason = "stop"
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
