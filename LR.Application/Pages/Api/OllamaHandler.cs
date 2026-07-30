using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Models.Ollama;

namespace LR.Application.Pages.Api;

/// <summary>
/// Ollama API handler.
/// Maps /api/chat and /api/tags endpoints.
/// </summary>
public class OllamaHandler : IProtocolHandler
{
    private readonly IServerManager _serverManager;
    private readonly IPresetManager _presetManager;
    private readonly IRoutingEngine _routingEngine;
    private readonly IRequestQueueService _queue;
    private readonly IStatisticsService _statisticsService;

    public ApiProtocol Protocol => ApiProtocol.Ollama;
    public string PathPrefix => "/api";

    public OllamaHandler(
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
        var models = presets.Select(p => new
        {
            name = p.Name,
            model = p.Name,
            size = 0L,
            digest = string.Empty,
            modified_at = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
        }).ToList();

        return new { models };
    }

    public async Task<object> HandleShowModelAsync(string modelName)
    {
        var presets = await _presetManager.GetAllPresetsAsync();
        var preset = presets.FirstOrDefault(p => p.Name == modelName);
        if (preset is null)
            return Microsoft.AspNetCore.Http.Results.NotFound($"Model '{modelName}' not found");

        // Build a Modelfile-like representation from the preset configuration
        var modelfileLines = new List<string>
        {
            $"# Modelfile for {preset.Name}",
            "",
            $"FROM \"{preset.ModelPath}\""
        };

        if (preset.ContextSize.HasValue && preset.ContextSize.Value > 0)
            modelfileLines.Add($"PARAMETER num_ctx {preset.ContextSize}");
        if (preset.GpuLayers.HasValue)
            modelfileLines.Add($"PARAMETER gpu_layers {preset.GpuLayers}");
        if (preset.Temperature.HasValue)
            modelfileLines.Add($"PARAMETER temperature {preset.Temperature}");
        if (preset.TopK.HasValue && preset.TopK.Value > 0)
            modelfileLines.Add($"PARAMETER top_k {preset.TopK}");
        if (preset.TopP.HasValue)
            modelfileLines.Add($"PARAMETER top_p {preset.TopP}");
        if (preset.MinP.HasValue)
            modelfileLines.Add($"PARAMETER min_p {preset.MinP}");
        if (preset.RepeatPenalty.HasValue && preset.RepeatPenalty.Value != 1.0f)
            modelfileLines.Add($"PARAMETER repeat_penalty {preset.RepeatPenalty}");
        if (preset.Threads.HasValue)
            modelfileLines.Add($"PARAMETER num_thread {preset.Threads}");
        if (preset.BatchSize.HasValue)
            modelfileLines.Add($"PARAMETER batch_size {preset.BatchSize}");

        var parameters = new Dictionary<string, string>();
        if (preset.ContextSize.HasValue && preset.ContextSize.Value > 0)
            parameters["num_ctx"] = preset.ContextSize.Value.ToString();
        if (preset.GpuLayers.HasValue)
            parameters["gpu_layers"] = preset.GpuLayers.Value.ToString();
        if (preset.Temperature.HasValue)
            parameters["temperature"] = preset.Temperature.Value.ToString("G");
        if (preset.TopK.HasValue && preset.TopK.Value > 0)
            parameters["top_k"] = preset.TopK.Value.ToString();
        if (preset.TopP.HasValue)
            parameters["top_p"] = preset.TopP.Value.ToString("G");
        if (preset.MinP.HasValue)
            parameters["min_p"] = preset.MinP.Value.ToString("G");
        if (preset.RepeatPenalty.HasValue && preset.RepeatPenalty.Value != 1.0f)
            parameters["repeat_penalty"] = preset.RepeatPenalty.Value.ToString("G");
        if (preset.Threads.HasValue)
            parameters["num_thread"] = preset.Threads.Value.ToString();
        if (preset.BatchSize.HasValue)
            parameters["batch_size"] = preset.BatchSize.Value.ToString();

        // Infer quantization from model path filename
        var fileName = Path.GetFileName(preset.ModelPath).ToLowerInvariant();
        string? quantLevel = null;
        if (fileName.Contains("q4_k_m") || fileName.Contains("q4_0")) quantLevel = "Q4_K_M";
        else if (fileName.Contains("q5_k_m")) quantLevel = "Q5_K_M";
        else if (fileName.Contains("q8_0")) quantLevel = "Q8_0";
        else if (fileName.Contains("f16") || fileName.Contains("f32")) quantLevel = "F16";

        return new ShowResponse
        {
            Modelfile = string.Join('\n', modelfileLines),
            Parameters = parameters.Count > 0 ? JsonSerializer.Serialize(parameters) : null,
            Projectors = null,
            Details = new ShowDetails
            {
                Format = "gguf",
                Family = "llama",
                Families = ["llama"],
                ParameterSize = null, // Not available from preset alone
                QuantizationLevel = quantLevel
            },
            Examine = null,
            Template = null
        };
    }


    public async Task<IResult> HandleChatCompletionAsync(HttpRequest httpRequest, HttpResponse httpResponse, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(httpRequest.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);
        var request = JsonSerializer.Deserialize<ChatRequest>(body);
        if (request is null) return Microsoft.AspNetCore.Http.Results.BadRequest("Invalid JSON in request body");

        // Build internal RouteRequest from the Ollama request
        var routeRequest = BuildRouteRequest(request);

        // Try to find a server immediately
        var server = await _routingEngine.RouteAsync(routeRequest, cancellationToken);
        if (server is not null && !server.IsBusy)
        {
            if (request.Stream)
            {
                httpResponse.Headers.ContentType = "application/x-ndjson";
                httpResponse.Headers.CacheControl = "no-cache";

                var provider = _serverManager.GetProvider(server.Id);
                if (provider is null)
                    return Microsoft.AspNetCore.Http.Results.Problem($"No backend provider registered for instance {server.Name}", statusCode: 503);

                server.IsBusy = true;
                try
                {
                    await foreach (var chunk in provider.SendStreamRequestAsync(routeRequest.Payload, cancellationToken))
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        if (chunk.IsFinal && chunk.Response is not null)
                        {
                            // Final chunk with done=true and metadata
                            await httpResponse.WriteAsync(JsonSerializer.Serialize(new ChatResponse
                            {
                                Model = request.Model,
                                CreatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                                Message = new LR.Core.Models.Ollama.ChatMessage { Role = "assistant", Content = string.Empty },
                                Done = true,
                                PromptEvalCount = chunk.Response.PromptTokensProcessed,
                                EvalCount = chunk.Response.GeneratedTokenCount,
                                TotalDuration = (long)chunk.Response.TotalLatencyMs * 1_000_000L
                            }) + "\n", cancellationToken);

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
                            await httpResponse.WriteAsync(JsonSerializer.Serialize(new ChatResponse
                            {
                                Model = request.Model,
                                CreatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                                Message = new LR.Core.Models.Ollama.ChatMessage { Role = "assistant", Content = chunk.TextDelta },
                                Done = false
                            }) + "\n", cancellationToken);
                        }

                        await httpResponse.Body.FlushAsync(cancellationToken);
                    }
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

        // Convert RouteResponse back to Ollama format
        return Microsoft.AspNetCore.Http.Results.Json(BuildChatResponse(request.Model, response));
    }

    private async Task<object> ProcessOnServer(
        ServerInstance server,
        ChatRequest chatRequest,
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

            return BuildChatResponse(chatRequest.Model, response);
        }
        finally
        {
            server.IsBusy = false;
        }
    }

    private RouteRequest BuildRouteRequest(ChatRequest request)
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

    private ChatResponse BuildChatResponse(string model, RouteResponse response)
    {
        return new ChatResponse
        {
            Model = model,
            CreatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Message = new LR.Core.Models.Ollama.ChatMessage { Role = "assistant", Content = response.Payload },
            Done = true,
            PromptEvalCount = response.PromptTokensProcessed,
            EvalCount = response.GeneratedTokenCount,
            TotalDuration = (long)response.TotalLatencyMs * 1_000_000L // nanoseconds
        };
    }
}
