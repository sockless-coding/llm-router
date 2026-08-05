using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Models.Ollama;
using LR.Core.Models.OpenAI;

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
    private readonly IGgufMetadataReader _ggufReader;

    public ApiProtocol Protocol => ApiProtocol.Ollama;
    public string PathPrefix => "/api";

    public OllamaHandler(
        IServerManager serverManager,
        IPresetManager presetManager,
        IRoutingEngine routingEngine,
        IRequestQueueService queue,
        IStatisticsService statisticsService,
        IGgufMetadataReader ggufReader)
    {
        _serverManager = serverManager;
        _presetManager = presetManager;
        _routingEngine = routingEngine;
        _queue = queue;
        _statisticsService = statisticsService;
        _ggufReader = ggufReader;
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

        // Read GGUF metadata from the model file for accurate, real-time data
        GgufMetadata? gguf = null;
        try
        {
            gguf = await _ggufReader.ReadAsync(preset.ModelPath);
        }
        catch { /* If we can't read the file, fall back to cached preset fields */ }

        // Build parameters text block (Ollama format: "key value" per line)
        var paramLines = new List<string>();
        if (preset.Temperature.HasValue)
            paramLines.Add($"temperature {preset.Temperature.Value}");
        if (preset.ContextSize.HasValue && preset.ContextSize.Value > 0)
            paramLines.Add($"num_ctx {preset.ContextSize.Value}");
        else if (gguf?.ContextLength.HasValue == true)
            paramLines.Add($"num_ctx {gguf.ContextLength.Value}");
        if (preset.GpuLayers.HasValue)
            paramLines.Add($"gpu_layers {preset.GpuLayers.Value}");
        if (preset.TopK.HasValue && preset.TopK.Value > 0)
            paramLines.Add($"top_k {preset.TopK.Value}");
        if (preset.TopP.HasValue)
            paramLines.Add($"top_p {preset.TopP.Value}");
        if (preset.MinP.HasValue)
            paramLines.Add($"min_p {preset.MinP.Value}");
        if (preset.RepeatPenalty.HasValue && preset.RepeatPenalty.Value != 1.0f)
            paramLines.Add($"repeat_penalty {preset.RepeatPenalty.Value}");
        if (preset.Threads.HasValue)
            paramLines.Add($"num_thread {preset.Threads.Value}");
        if (preset.BatchSize.HasValue)
            paramLines.Add($"batch_size {preset.BatchSize.Value}");

        // Determine architecture from GGUF data or cached preset field, falling back to "llama"
        var architecture = gguf?.Architecture ?? preset.GgufArchitecture ?? "llama";

        // Build details from GGUF metadata (or fall back to cached fields)
        var quantLevel = gguf?.QuantizationLevel ?? preset.GgufQuantizationLevel;
        var parameterSize = gguf?.ParameterSize ?? preset.GgufParameterSize;

        // Get modified_at from the GGUF file's last write time
        string? modifiedAt = null;
        try
        {
            if (File.Exists(preset.ModelPath))
                modifiedAt = File.GetLastWriteTimeUtc(preset.ModelPath).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        }
        catch { /* Can't read file time */ }

        // Infer capabilities from architecture
        var capabilities = new List<string> { "completion" };
        if (architecture.Contains("mllama") || architecture.Contains("clip"))
            capabilities.Add("vision");

        return new ShowResponse
        {
            Parameters = paramLines.Count > 0 ? string.Join('\n', paramLines) : null,
            License = gguf?.LicenseText,
            ModifiedAt = modifiedAt,
            Details = new ShowDetails
            {
                ParentModel = gguf?.ModelName ?? preset.GgufModelName,
                Format = "gguf",
                Family = architecture,
                Families = [architecture],
                ParameterSize = parameterSize,
                QuantizationLevel = quantLevel
            },
            Template = gguf?.ChatTemplate ?? preset.GgufChatTemplate,
            Capabilities = capabilities.ToArray(),
            ModelInfo = gguf?.AllKvPairs
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
        if (server != null)
        {
            if (request.Stream)
            {
                httpResponse.StatusCode = 200;
                httpResponse.Headers.ContentType = "application/x-ndjson";
                httpResponse.Headers.CacheControl = "no-cache";

                var provider = _serverManager.GetProvider(server.Id);
                if (provider is null)
                    return Microsoft.AspNetCore.Http.Results.Problem($"No backend provider registered for instance {server.Name}", statusCode: 503);

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
                            DoneReason = "stop",
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

                return Results.Empty;
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

    /// <summary>
    /// Handle /api/generate endpoint — text generation with a single prompt.
    /// Converts the generate request to an internal chat-style request for routing.
    /// </summary>
    public async Task<IResult> HandleGenerateCompletionAsync(HttpRequest httpRequest, HttpResponse httpResponse, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(httpRequest.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);
        var request = JsonSerializer.Deserialize<GenerateRequest>(body);
        if (request is null) return Microsoft.AspNetCore.Http.Results.BadRequest("Invalid JSON in request body");

        // Convert generate request to chat-style internal request
        var routeRequest = BuildRouteRequestFromGenerate(request);

        // Try to find a server immediately
        var server = await _routingEngine.RouteAsync(routeRequest, cancellationToken);
        if (server != null)
        {
            var provider = _serverManager.GetProvider(server.Id);
            if (provider is null)
                return Microsoft.AspNetCore.Http.Results.Problem($"No backend provider registered for instance {server.Name}", statusCode: 503);

            if (request.Stream)
            {
                httpResponse.StatusCode = 200;
                httpResponse.Headers.ContentType = "application/x-ndjson";
                httpResponse.Headers.CacheControl = "no-cache";

                await foreach (var chunk in provider.SendStreamRequestAsync(routeRequest.Payload, cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    if (chunk.IsFinal && chunk.Response is not null)
                    {
                        // Final chunk with done=true and metadata
                        await httpResponse.WriteAsync(JsonSerializer.Serialize(new GenerateResponse
                        {
                            Model = request.Model,
                            CreatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                            Response = string.Empty,
                            Done = true,
                            DoneReason = "stop",
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
                        await httpResponse.WriteAsync(JsonSerializer.Serialize(new GenerateResponse
                        {
                            Model = request.Model,
                            CreatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                            Response = chunk.TextDelta,
                            Done = false
                        }) + "\n", cancellationToken);
                    }

                    await httpResponse.Body.FlushAsync(cancellationToken);
                }

                return Results.Empty;
            }

            // Non-streaming: process on server and return full response

            var result = await provider.SendRequestAsync(routeRequest.Payload, cancellationToken);
            if (result == null)
                return Microsoft.AspNetCore.Http.Results.Problem("Backend returned no response", statusCode: 502);

            // Record statistics
            try
            {
                var presetId = routeRequest.PresetId ?? server.ActivePresetId;
                var preset = presetId.HasValue ? _presetManager.GetById(presetId.Value) : null;
                await _statisticsService.RecordRequestAsync(server, preset, result);
            }
            catch { /* Stats recording failure shouldn't block the response */ }

            return Microsoft.AspNetCore.Http.Results.Json(new GenerateResponse
            {
                Model = request.Model,
                CreatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                Response = result.Payload,
                Done = true,
                DoneReason = "stop",
                PromptEvalCount = result.PromptTokensProcessed,
                EvalCount = result.GeneratedTokenCount,
                TotalDuration = (long)result.TotalLatencyMs * 1_000_000L
            });
        }

        // Queue the request
        var response = await _queue.EnqueueAsync(routeRequest, cancellationToken);

        return Microsoft.AspNetCore.Http.Results.Json(new GenerateResponse
        {
            Model = request.Model,
            CreatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Response = response.Payload,
            Done = true,
            DoneReason = "stop",
            PromptEvalCount = response.PromptTokensProcessed,
            EvalCount = response.GeneratedTokenCount,
            TotalDuration = (long)response.TotalLatencyMs * 1_000_000L
        });
    }

    /// <summary>
    /// Handle /api/embed endpoint — generate embeddings from a model.
    /// </summary>
    public async Task<object> HandleEmbeddingsAsync(EmbedRequest request)
    {
        // Normalize input to a list of strings
        var inputs = new List<string>();
        if (request.Input is string singleInput)
            inputs.Add(singleInput);
        else if (request.Input is JsonElement jsonElem && jsonElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in jsonElem.EnumerateArray())
                inputs.Add(item.GetString() ?? string.Empty);
        }

        // Find a running server for the requested model
        var presets = await _presetManager.GetAllPresetsAsync();
        var preset = presets.FirstOrDefault(p => p.Name == request.Model);
        if (preset is null)
            return Microsoft.AspNetCore.Http.Results.NotFound($"Model '{request.Model}' not found");

        // For now, return a placeholder embedding response.
        // A real implementation would send the embeddings request to the backend provider.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var dummyEmbeddings = inputs.Select(_ => Enumerable.Range(0, 384).Select(_ => (double)(new Random().NextDouble() * 2 - 1)).ToList()).ToList();
        sw.Stop();

        return new EmbedResponse
        {
            Model = request.Model,
            Embeddings = dummyEmbeddings,
            TotalDuration = (long)sw.Elapsed.TotalMilliseconds * 1_000_000L,
            PromptEvalCount = inputs.Sum(s => s.Length / 4)
        };
    }

    /// <summary>
    /// Handle /api/ps endpoint — list models currently loaded in memory.
    /// </summary>
    public async Task<object> HandlePsAsync()
    {
        var instances = _serverManager.GetAllInstances();
        var runningModels = new List<PsModelInfo>();

        foreach (var instance in instances)
        {
            if (instance.Status != ServerStatus.Running || !instance.IsHealthy)
                continue;

            var presetId = instance.ActivePresetId;
            if (!presetId.HasValue)
                continue;

            var preset = _presetManager.GetById(presetId.Value);
            if (preset is null)
                continue;

            // Get actual model file size
            long modelSize = 0L;
            try
            {
                if (File.Exists(preset.ModelPath))
                    modelSize = new FileInfo(preset.ModelPath).Length;
            }
            catch { /* File may not be accessible */ }

            // Infer quantization from model path filename
            var fileName = Path.GetFileName(preset.ModelPath).ToLowerInvariant();
            string? quantLevel = null;
            if (fileName.Contains("q4_k_m") || fileName.Contains("q4_0")) quantLevel = "Q4_K_M";
            else if (fileName.Contains("q5_k_m")) quantLevel = "Q5_K_M";
            else if (fileName.Contains("q8_0")) quantLevel = "Q8_0";
            else if (fileName.Contains("f16") || fileName.Contains("f32")) quantLevel = "F16";

            runningModels.Add(new PsModelInfo
            {
                Name = preset.Name,
                Model = preset.Name,
                Size = modelSize,
                Digest = string.Empty,
                Details = new PsModelDetails
                {
                    Format = "gguf",
                    Family = "llama",
                    Families = ["llama"],
                    QuantizationLevel = quantLevel
                },
                ExpiresAt = null // Models stay loaded while server is running
            });
        }

        return new { models = runningModels };
    }

    private RouteRequest BuildRouteRequest(ChatRequest ollamaRequest)
    {
        // Find preset matching the model name
        var presets = _presetManager.GetAllPresets();
        var preset = presets.FirstOrDefault(p => p.Name == ollamaRequest.Model);

        // Convert Ollama ChatRequest to OpenAI-compatible format for backend providers
        var openAiRequest = new ChatCompletionRequest
        {
            Model = ollamaRequest.Model,
            Stream = ollamaRequest.Stream,
            Temperature = ollamaRequest.Options?.Temperature,
            TopP = ollamaRequest.Options?.TopP,
            MaxTokens = ollamaRequest.Options?.NumPredict,
            Stop = ollamaRequest.Options?.Stop,
            Messages = ollamaRequest.Messages.Select(m => new LR.Core.Models.OpenAI.ChatMessage
            {
                Role = m.Role,
                Content = m.Content
            }).ToList()
        };

        return new RouteRequest
        {
            ModelName = ollamaRequest.Model,
            PresetId = preset?.Id,
            Payload = JsonSerializer.Serialize(openAiRequest)
        };
    }

    /// <summary>
    /// Build a RouteRequest from an Ollama GenerateRequest by converting it to chat-style.
    /// </summary>
    private RouteRequest BuildRouteRequestFromGenerate(GenerateRequest generateRequest)
    {
        // Find preset matching the model name
        var presets = _presetManager.GetAllPresets();
        var preset = presets.FirstOrDefault(p => p.Name == generateRequest.Model);

        // Convert generate request to OpenAI chat format (single user message with prompt)
        var messages = new List<LR.Core.Models.OpenAI.ChatMessage>();
        if (!string.IsNullOrEmpty(generateRequest.System))
            messages.Add(new LR.Core.Models.OpenAI.ChatMessage { Role = "system", Content = generateRequest.System });
        messages.Add(new LR.Core.Models.OpenAI.ChatMessage { Role = "user", Content = generateRequest.Prompt });

        var openAiRequest = new ChatCompletionRequest
        {
            Model = generateRequest.Model,
            Stream = generateRequest.Stream,
            Temperature = generateRequest.Options?.Temperature,
            TopP = generateRequest.Options?.TopP,
            MaxTokens = generateRequest.Options?.NumPredict,
            Stop = generateRequest.Options?.Stop,
            Messages = messages
        };

        return new RouteRequest
        {
            ModelName = generateRequest.Model,
            PresetId = preset?.Id,
            Payload = JsonSerializer.Serialize(openAiRequest)
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
            DoneReason = "stop",
            PromptEvalCount = response.PromptTokensProcessed,
            EvalCount = response.GeneratedTokenCount,
            TotalDuration = (long)response.TotalLatencyMs * 1_000_000L // nanoseconds
        };
    }
}
