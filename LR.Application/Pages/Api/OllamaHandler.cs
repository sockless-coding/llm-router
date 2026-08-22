using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    private readonly IChatTemplateVariableExtractor _templateVariableExtractor;
    private readonly IApiRequestLogger _requestLogger;
    private readonly GatewaySettings _gatewaySettings;
    private readonly IApiKeyRequestContext _apiKeyContext;

    public ApiProtocol Protocol => ApiProtocol.Ollama;
    public string PathPrefix => "/api";

    public OllamaHandler(
        IServerManager serverManager,
        IPresetManager presetManager,
        IRoutingEngine routingEngine,
        IRequestQueueService queue,
        IStatisticsService statisticsService,
        IGgufMetadataReader ggufReader,
        IChatTemplateVariableExtractor templateVariableExtractor,
        IApiRequestLogger requestLogger,
        GatewaySettings gatewaySettings,
        IApiKeyRequestContext apiKeyContext)
    {
        _serverManager = serverManager;
        _presetManager = presetManager;
        _routingEngine = routingEngine;
        _queue = queue;
        _statisticsService = statisticsService;
        _ggufReader = ggufReader;
        _templateVariableExtractor = templateVariableExtractor;
        _requestLogger = requestLogger;
        _gatewaySettings = gatewaySettings;
        _apiKeyContext = apiKeyContext;
    }

    public async Task<object> HandleListModelsAsync()
    {
        var presets = await _presetManager.GetAllPresetsAsync();
        var models = _apiKeyContext.FilterAllowed(presets).Select(p =>
        {
            var architecture = p.GgufArchitecture ?? "llama";

            // Infer capabilities from architecture and chat template — mirrors HandleShowModelAsync so
            // clients that only call /api/tags (e.g. Visual Studio's Copilot Ollama
            // provider) see the same capabilities as /api/show.
            var capabilities = new List<string> { "completion" };
            if (architecture.Contains("mllama") || architecture.Contains("clip"))
                capabilities.Add("vision");
            if (SupportsTools(p.GgufChatTemplate))
                capabilities.Add("tools");
            if (SupportsThinking(p.Reasoning, p.GgufChatTemplate, _templateVariableExtractor))
                capabilities.Add("thinking");

            long modelSize = 0L;
            try
            {
                if (File.Exists(p.ModelPath))
                    modelSize = new FileInfo(p.ModelPath).Length;
            }
            catch { /* File may not be accessible */ }

            return new
            {
                name = p.Name,
                model = p.Name,
                size = modelSize,
                digest = string.Empty,
                modified_at = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                details = new
                {
                    parent_model = p.GgufModelName,
                    format = "gguf",
                    family = architecture,
                    families = new[] { architecture },
                    parameter_size = p.GgufParameterSize,
                    quantization_level = p.GgufQuantizationLevel,
                    // Ollama nests context/embedding length inside "details", not as a
                    // top-level sibling of "capabilities" — clients like VS Code's Copilot
                    // Ollama provider read context length from here.
                    context_length = p.GgufContextLength,
                    embedding_length = p.GgufEmbeddingLength
                },
                capabilities
            };
        }).ToList();

        return new { models };
    }

    public async Task<object> HandleShowModelAsync(string modelName)
    {
        var presets = await _presetManager.GetAllPresetsAsync();
        var preset = presets.FirstOrDefault(p => p.Name == modelName);
        if (preset is null || !_apiKeyContext.IsModelAllowed(preset.Id))
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

        var template = gguf?.ChatTemplate ?? preset.GgufChatTemplate;

        // Infer capabilities from architecture and chat template
        var capabilities = new List<string> { "completion" };
        if (architecture.Contains("mllama") || architecture.Contains("clip"))
            capabilities.Add("vision");
        if (SupportsTools(template))
            capabilities.Add("tools");
        if (SupportsThinking(preset.Reasoning, template, _templateVariableExtractor))
            capabilities.Add("thinking");

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
            Template = template,
            Capabilities = capabilities.ToArray(),
            ModelInfo = gguf?.AllKvPairs
        };
    }

    /// <summary>
    /// Ollama reports "tools" capability when the model's chat template renders a tool-call
    /// block (Jinja templates gate this behind an "{% if tools %}"-style check). Mirroring that
    /// via a template scan keeps Copilot's agent/tool-use mode enabled for models routed through
    /// us the same way it is when Copilot talks to Ollama directly.
    /// </summary>
    /// <summary>
    /// "tools" capability check deliberately stays a raw substring scan rather than using
    /// IChatTemplateVariableExtractor: "tools" is part of the standard llama.cpp/minja render
    /// context, so the extractor's free-variable allowlist filters it out by design — it has
    /// nothing to add here.
    /// </summary>
    private static bool SupportsTools(string? chatTemplate) =>
        !string.IsNullOrEmpty(chatTemplate) && chatTemplate.Contains("tools", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Free variable names that, when a chat template reads them, signal a reasoning/thinking
    /// toggle the template exposes via --chat-template-kwargs.
    /// </summary>
    private static readonly HashSet<string> ReasoningSignalVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "enable_thinking", "thinking", "reasoning_effort", "thinking_budget"
    };

    /// <summary>
    /// "thinking" capability: the preset has reasoning explicitly enabled, or the chat template
    /// itself emits a thinking block (e.g. "&lt;think&gt;") or references a known reasoning-toggle
    /// variable (e.g. "enable_thinking", "reasoning_effort") detected via
    /// <see cref="IChatTemplateVariableExtractor"/>.
    /// </summary>
    private static bool SupportsThinking(string? reasoning, string? chatTemplate, IChatTemplateVariableExtractor templateVariableExtractor)
    {
        if (!string.IsNullOrEmpty(reasoning) && !reasoning.Equals("off", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(chatTemplate) && chatTemplate.Contains("<think>", StringComparison.OrdinalIgnoreCase))
            return true;

        return templateVariableExtractor.Extract(chatTemplate).Any(v => ReasoningSignalVariables.Contains(v.Name));
    }


    public async Task<IResult> HandleChatCompletionAsync(HttpRequest httpRequest, HttpResponse httpResponse, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(httpRequest.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);
        var request = JsonSerializer.Deserialize<ChatRequest>(body);
        if (request is null) return Microsoft.AspNetCore.Http.Results.BadRequest("Invalid JSON in request body");

        // Reject models this API key isn't scoped to before touching the routing engine —
        // RoutingEngine's round-robin fallback would otherwise happily route an unresolved
        // model name to any healthy server, silently bypassing the scoping.
        var requestedPreset = _presetManager.GetAllPresets().FirstOrDefault(p => p.Name == request.Model);
        if (requestedPreset is not null && !_apiKeyContext.IsModelAllowed(requestedPreset.Id))
        {
            return Microsoft.AspNetCore.Http.Results.Json(new { error = $"Model '{request.Model}' is not accessible with this API key." }, statusCode: 403);
        }

        // Build internal RouteRequest from the Ollama request
        var routeRequest = BuildRouteRequest(request);

        // Log incoming request
        Guid logId = Guid.Empty;
        try { logId = await _requestLogger.LogIncomingAsync(ApiProtocol.Ollama, "/api/chat", body, request.Model); }
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
                httpResponse.Headers.ContentType = "application/x-ndjson";
                httpResponse.Headers.CacheControl = "no-cache";
                // Disable Kestrel response buffering so writes go directly to the socket
                httpResponse.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

                var provider = _serverManager.GetProvider(server.Id);
                if (provider is null)
                    return Microsoft.AspNetCore.Http.Results.Problem($"No backend provider registered for instance {server.Name}", statusCode: 503);

                // Note: no early "if cancellationToken.IsCancellationRequested break" guard here —
                // once cancelled (client disconnect), the provider stops reading from llama.cpp
                // and yields exactly one more chunk: a synthetic final chunk carrying whatever
                // partial content/stats it captured. Breaking before that chunk is processed
                // would discard it and skip the stats recording below.
                await foreach (var chunk in provider.SendStreamRequestAsync(routeRequest.Payload, routeRequest.Protocol, cancellationToken))
                {
                    if (chunk.IsFinal && chunk.Response is not null)
                    {
                        // Final chunk with done=true and metadata. Best-effort write — the client
                        // may already be gone (that's the same disconnect that just cancelled the
                        // backend call above) — don't let a failed write skip stats recording.
                        try
                        {
                            await httpResponse.WriteAsync(JsonSerializer.Serialize(new ChatResponse
                            {
                                Model = request.Model,
                                CreatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                                Message = new LR.Core.Models.Ollama.ChatMessage
                                {
                                    Role = "assistant",
                                    Content = string.Empty,
                                    ToolCalls = MapToolCallsToOllama(chunk.Response.ToolCalls)
                                },
                                Done = true,
                                DoneReason = "stop",
                                PromptEvalCount = chunk.Response.PromptTokensProcessed,
                                EvalCount = chunk.Response.GeneratedTokenCount,
                                TotalDuration = (long)chunk.Response.TotalLatencyMs * 1_000_000L
                            }) + "\n", cancellationToken);
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
                    }
                    else if (!string.IsNullOrEmpty(chunk.TextDelta) || !string.IsNullOrEmpty(chunk.ReasoningContentDelta))
                    {
                        // Concatenate reasoning content with text for Ollama's simple Content field
                        var content = string.Concat(chunk.ReasoningContentDelta, chunk.TextDelta);
                        await httpResponse.WriteAsync(JsonSerializer.Serialize(new ChatResponse
                        {
                            Model = request.Model,
                            CreatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                            Message = new LR.Core.Models.Ollama.ChatMessage { Role = "assistant", Content = content },
                            Done = false
                        }) + "\n", cancellationToken);
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

        // Convert RouteResponse back to Ollama format
        return Microsoft.AspNetCore.Http.Results.Json(BuildChatResponse(request.Model, response));
    }

    private async Task<object> ProcessOnServer(
        ServerInstance server,
        ChatRequest chatRequest,
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

        // Reject models this API key isn't scoped to before touching the routing engine —
        // RoutingEngine's round-robin fallback would otherwise happily route an unresolved
        // model name to any healthy server, silently bypassing the scoping.
        var requestedPreset = _presetManager.GetAllPresets().FirstOrDefault(p => p.Name == request.Model);
        if (requestedPreset is not null && !_apiKeyContext.IsModelAllowed(requestedPreset.Id))
        {
            return Microsoft.AspNetCore.Http.Results.Json(new { error = $"Model '{request.Model}' is not accessible with this API key." }, statusCode: 403);
        }

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
                // Disable Kestrel response buffering so writes go directly to the socket
                httpResponse.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

                await foreach (var chunk in provider.SendStreamRequestAsync(routeRequest.Payload, routeRequest.Protocol, cancellationToken))
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
                    else if (!string.IsNullOrEmpty(chunk.TextDelta) || !string.IsNullOrEmpty(chunk.ReasoningContentDelta))
                    {
                        // Concatenate reasoning content with text for Ollama's simple Response field
                        var responseText = string.Concat(chunk.ReasoningContentDelta, chunk.TextDelta);
                        await httpResponse.WriteAsync(JsonSerializer.Serialize(new GenerateResponse
                        {
                            Model = request.Model,
                            CreatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                            Response = responseText,
                            Done = false
                        }) + "\n", cancellationToken);
                    }

                    await httpResponse.Body.FlushAsync(cancellationToken);
                }

                return Results.Empty;
            }

            // Non-streaming: process on server and return full response

            var result = await provider.SendRequestAsync(routeRequest.Payload, routeRequest.Protocol, cancellationToken);
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
        if (preset is null || !_apiKeyContext.IsModelAllowed(preset.Id))
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
            if (preset is null || !_apiKeyContext.IsModelAllowed(preset.Id))
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
            Temperature = ollamaRequest.Options != null && ollamaRequest.Options.Temperature.HasValue ? (float)ollamaRequest.Options.Temperature.Value : null,
            TopP = ollamaRequest.Options != null && ollamaRequest.Options.TopP.HasValue ? (float)ollamaRequest.Options.TopP.Value : null,
            TopK = ollamaRequest.Options?.TopK,
            MinP = ollamaRequest.Options != null && ollamaRequest.Options.MinP.HasValue ? (float)ollamaRequest.Options.MinP.Value : null,
            RepeatPenalty = ollamaRequest.Options != null && ollamaRequest.Options.RepeatPenalty.HasValue ? (float)ollamaRequest.Options.RepeatPenalty.Value : null,
            PresencePenalty = ollamaRequest.Options != null && ollamaRequest.Options.PresencePenalty.HasValue ? (float)ollamaRequest.Options.PresencePenalty.Value : null,
            FrequencyPenalty = ollamaRequest.Options != null && ollamaRequest.Options.FrequencyPenalty.HasValue ? (float)ollamaRequest.Options.FrequencyPenalty.Value : null,
            Seed = ollamaRequest.Options?.Seed,
            MaxTokens = ollamaRequest.Options?.NumPredict,
            Stop = ollamaRequest.Options?.Stop,
            Messages = ConvertOllamaMessages(ollamaRequest.Messages),
            Tools = ollamaRequest.Tools
        };

        // llama.cpp (like OpenAI) only includes token usage in the SSE stream when
        // stream_options.include_usage is set. Force it on so prompt_eval_count/eval_count
        // are populated in the final streamed chunk instead of always reading 0.
        if (openAiRequest.Stream)
        {
            openAiRequest.StreamOptions = new StreamOptions { IncludeUsage = true };
        }

        return new RouteRequest
        {
            ModelName = ollamaRequest.Model,
            PresetId = preset?.Id,
            // Omit null fields — backend rejects "name":null etc.
            Payload = JsonSerializer.Serialize(openAiRequest, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull })
        };
    }

    /// <summary>
    /// Converts Ollama-protocol messages to OpenAI-compatible messages, preserving tool calls
    /// and tool results. Ollama assistant messages carry tool_calls with arguments as a JSON
    /// object and no reliable call ID; tool-result messages identify the call by tool name
    /// (via "tool_name") rather than by ID. OpenAI/llama.cpp instead correlate tool results to
    /// calls by ID and require assistant messages to carry either non-empty content or
    /// tool_calls — never neither, and never an empty-string content alongside dropped
    /// tool_calls, which is what silently happened here before this method existed, producing
    /// backend 400s ("Assistant message must contain either 'content' or 'tool_calls'!") on any
    /// follow-up turn after a tool call. This synthesizes an ID per tool call (if the client
    /// didn't supply one) and backfills it onto the matching tool-result message by name.
    /// </summary>
    private static List<LR.Core.Models.OpenAI.ChatMessage> ConvertOllamaMessages(List<LR.Core.Models.Ollama.ChatMessage> ollamaMessages)
    {
        var result = new List<LR.Core.Models.OpenAI.ChatMessage>(ollamaMessages.Count);
        var lastToolCallIdByName = new Dictionary<string, string>();

        foreach (var m in ollamaMessages)
        {
            List<LR.Core.Models.OpenAI.ChatToolCall>? toolCalls = null;
            if (m.ToolCalls is { Count: > 0 })
            {
                toolCalls = new List<LR.Core.Models.OpenAI.ChatToolCall>(m.ToolCalls.Count);
                foreach (var tc in m.ToolCalls)
                {
                    var id = string.IsNullOrEmpty(tc.Id) ? $"call_{Guid.NewGuid():N}" : tc.Id;
                    if (!string.IsNullOrEmpty(tc.Function.Name))
                        lastToolCallIdByName[tc.Function.Name] = id;

                    toolCalls.Add(new LR.Core.Models.OpenAI.ChatToolCall
                    {
                        Id = id,
                        Type = "function",
                        Function = new LR.Core.Models.OpenAI.ChatToolCallFunction
                        {
                            Name = tc.Function.Name,
                            Arguments = tc.Function.Arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                                ? "{}"
                                : tc.Function.Arguments.GetRawText()
                        }
                    });
                }
            }

            string? toolCallId = m.ToolCallId;
            if (m.Role == "tool" && toolCallId is null && m.ToolName is not null)
                lastToolCallIdByName.TryGetValue(m.ToolName, out toolCallId);

            // Only omit content when tool_calls are present to satisfy the "content or
            // tool_calls" requirement — for every other role (including empty tool results),
            // keep sending an explicit empty string rather than null, since null content with
            // no tool_calls is exactly the shape backends reject.
            var content = string.IsNullOrEmpty(m.Content) && toolCalls is not null
                ? null
                : ChatMessageContent.FromText(m.Content);

            result.Add(new LR.Core.Models.OpenAI.ChatMessage
            {
                Role = m.Role,
                Content = content,
                ToolCalls = toolCalls,
                ToolCallId = toolCallId,
                Name = m.Role == "tool" ? m.ToolName : null
            });
        }

        return result;
    }

    /// <summary>
    /// Converts backend tool calls (OpenAI shape: arguments as a JSON-encoded string) back to
    /// Ollama's shape (arguments as a JSON object), so Ollama-protocol clients that request
    /// tool use actually see the resulting calls instead of them being silently dropped.
    /// </summary>
    private static List<OllamaToolCall>? MapToolCallsToOllama(List<LR.Core.Models.OpenAI.ChatToolCall>? toolCalls)
    {
        if (toolCalls is null || toolCalls.Count == 0) return null;

        return toolCalls.Select(tc =>
        {
            JsonElement arguments;
            try
            {
                arguments = JsonDocument.Parse(string.IsNullOrEmpty(tc.Function.Arguments) ? "{}" : tc.Function.Arguments).RootElement.Clone();
            }
            catch (JsonException)
            {
                arguments = JsonDocument.Parse("{}").RootElement.Clone();
            }

            return new OllamaToolCall
            {
                Id = tc.Id,
                Function = new OllamaToolCallFunction { Name = tc.Function.Name, Arguments = arguments }
            };
        }).ToList();
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
            messages.Add(new LR.Core.Models.OpenAI.ChatMessage { Role = "system", Content = ChatMessageContent.FromText(generateRequest.System) });
        messages.Add(new LR.Core.Models.OpenAI.ChatMessage { Role = "user", Content = ChatMessageContent.FromText(generateRequest.Prompt) });

        var openAiRequest = new ChatCompletionRequest
        {
            Model = generateRequest.Model,
            Stream = generateRequest.Stream,
            Temperature = generateRequest.Options != null && generateRequest.Options.Temperature.HasValue ? (float)generateRequest.Options.Temperature.Value : null,
            TopP = generateRequest.Options != null && generateRequest.Options.TopP.HasValue ? (float)generateRequest.Options.TopP.Value : null,
            TopK = generateRequest.Options?.TopK,
            MinP = generateRequest.Options != null && generateRequest.Options.MinP.HasValue ? (float)generateRequest.Options.MinP.Value : null,
            RepeatPenalty = generateRequest.Options != null && generateRequest.Options.RepeatPenalty.HasValue ? (float)generateRequest.Options.RepeatPenalty.Value : null,
            PresencePenalty = generateRequest.Options != null && generateRequest.Options.PresencePenalty.HasValue ? (float)generateRequest.Options.PresencePenalty.Value : null,
            FrequencyPenalty = generateRequest.Options != null && generateRequest.Options.FrequencyPenalty.HasValue ? (float)generateRequest.Options.FrequencyPenalty.Value : null,
            Seed = generateRequest.Options?.Seed,
            MaxTokens = generateRequest.Options?.NumPredict,
            Stop = generateRequest.Options?.Stop,
            Messages = messages
        };

        // llama.cpp (like OpenAI) only includes token usage in the SSE stream when
        // stream_options.include_usage is set. Force it on so prompt_eval_count/eval_count
        // are populated in the final streamed chunk instead of always reading 0.
        if (openAiRequest.Stream)
        {
            openAiRequest.StreamOptions = new StreamOptions { IncludeUsage = true };
        }

        return new RouteRequest
        {
            ModelName = generateRequest.Model,
            PresetId = preset?.Id,
            // Omit null fields — backend rejects "name":null etc.
            Payload = JsonSerializer.Serialize(openAiRequest, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull })
        };
    }

    private ChatResponse BuildChatResponse(string model, RouteResponse response)
    {
        return new ChatResponse
        {
            Model = model,
            CreatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Message = new LR.Core.Models.Ollama.ChatMessage
            {
                Role = "assistant",
                Content = response.Payload,
                ToolCalls = MapToolCallsToOllama(response.ToolCalls)
            },
            Done = true,
            DoneReason = "stop",
            PromptEvalCount = response.PromptTokensProcessed,
            EvalCount = response.GeneratedTokenCount,
            TotalDuration = (long)response.TotalLatencyMs * 1_000_000L // nanoseconds
        };
    }
}
