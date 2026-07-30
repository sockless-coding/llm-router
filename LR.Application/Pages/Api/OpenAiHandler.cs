using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

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
    private readonly IServerManager _serverManager;
    private readonly IPresetManager _presetManager;
    private readonly IRoutingEngine _routingEngine;
    private readonly IRequestQueueService _queue;
    private readonly IStatisticsService _statisticsService;

    public ApiProtocol Protocol => ApiProtocol.OpenAI;
    public string PathPrefix => "/v1";

    public OpenAiHandler(
        IServerManager serverManager,
        IPresetManager presetManager,
        IRoutingEngine routingEngine,
        IRequestQueueService queue,
        IStatisticsService statisticsService)
    {
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
        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(body);
        if (request is null) return Microsoft.AspNetCore.Http.Results.BadRequest("Invalid JSON in request body");

        // Build internal RouteRequest from the OpenAI request
        var routeRequest = BuildRouteRequest(request);

        // Try to find a server immediately
        var server = await _routingEngine.RouteAsync(routeRequest, cancellationToken);
        if (server is not null && !server.IsBusy)
        {
            if (request.Stream)
            {
                httpResponse.Headers.ContentType = "text/event-stream";
                httpResponse.Headers.CacheControl = "no-cache";

                var provider = _serverManager.GetProvider(server.Id);
                if (provider is null)
                    return Microsoft.AspNetCore.Http.Results.Problem($"No backend provider registered for instance {server.Name}", statusCode: 503);

                server.IsBusy = true;
                try
                {
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
                                Delta = new DeltaMessage { Role = "assistant", Content = string.Empty }
                            }
                        }
                    })}\n", cancellationToken);
                    await httpResponse.Body.FlushAsync(cancellationToken);

                    await foreach (var chunk in provider.SendStreamRequestAsync(routeRequest.Payload, cancellationToken))
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        if (chunk.IsFinal && chunk.Response is not null)
                        {
                            // Final chunk with finish_reason
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
                                }
                            })}\n", cancellationToken);

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
                                        Delta = new DeltaMessage { Content = chunk.TextDelta }
                                    }
                                }
                            })}\n", cancellationToken);
                        }

                        await httpResponse.Body.FlushAsync(cancellationToken);
                    }

                    // Signal end of stream
                    await httpResponse.WriteAsync("data: [DONE]\n", cancellationToken);
                    await httpResponse.Body.FlushAsync(cancellationToken);
                }
                finally
                {
                    server.IsBusy = false;
                }

                return Microsoft.AspNetCore.Http.Results.Ok();
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
        server.IsBusy = true;

        try
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
        finally
        {
            server.IsBusy = false;
        }
    }

    private RouteRequest BuildRouteRequest(ChatCompletionRequest request)
    {
        // Find preset matching the model name
        var presets = _presetManager.GetAllPresets();
        var preset = presets.FirstOrDefault(p => p.Name == request.Model);

        return new RouteRequest
        {
            ModelName = request.Model,
            PresetId = preset?.Id,
            Payload = JsonSerializer.Serialize(request)
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
                    Message = new ChatMessage { Role = "assistant", Content = response.Payload },
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
