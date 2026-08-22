using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Models.OpenAI;
using LR.Core.Models.OpenAI.Responses;
using LR.Core.Services;

namespace LR.Application.Pages.Api;

/// <summary>
/// OpenAI Responses API handler. Maps POST/GET/DELETE /v1/responses[/{id}] and
/// POST /v1/responses/{id}/cancel. llama.cpp only speaks the Chat Completions shape, so every
/// request here is translated into a <see cref="ChatCompletionRequest"/> under the hood (reusing
/// the same RouteRequest/IBackendProvider plumbing as <see cref="OpenAiHandler"/>) and the
/// backend's reply is translated back into Responses API output items.
///
/// Kept separate from <see cref="OpenAiHandler"/>: different request/response DTOs, a different
/// SSE event vocabulary, and a persistence model (<see cref="StoredResponse"/>) that Chat
/// Completions has no equivalent of.
///
/// Not supported (llama.cpp/local-router non-goals): OpenAI built-in tools (web_search,
/// file_search, code_interpreter, computer_use, image_generation, mcp) — only "function" tools
/// work; the Conversations API; `include` selectors; true SSE stream resumption via
/// `starting_after`; reasoning "summary" synthesis (only raw reasoning_content pass-through).
/// </summary>
public class ResponsesHandler
{
    private static readonly JsonSerializerOptions BackendJsonOpts = new() { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly HashSet<string> TerminalStatuses = new() { "completed", "failed", "cancelled", "incomplete" };

    private readonly ILogger<ResponsesHandler> _logger;
    private readonly IServerManager _serverManager;
    private readonly IPresetManager _presetManager;
    private readonly IRoutingEngine _routingEngine;
    private readonly IRequestQueueService _queue;
    private readonly IStatisticsService _statisticsService;
    private readonly IApiRequestLogger _requestLogger;
    private readonly GatewaySettings _gatewaySettings;
    private readonly LRDbContext _db;
    private readonly ResponseChainBuilder _chainBuilder;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBackgroundResponseRegistry _registry;
    private readonly IApiKeyRequestContext _apiKeyContext;

    public ResponsesHandler(
        ILogger<ResponsesHandler> logger,
        IServerManager serverManager,
        IPresetManager presetManager,
        IRoutingEngine routingEngine,
        IRequestQueueService queue,
        IStatisticsService statisticsService,
        IApiRequestLogger requestLogger,
        GatewaySettings gatewaySettings,
        LRDbContext db,
        ResponseChainBuilder chainBuilder,
        IServiceScopeFactory scopeFactory,
        IBackgroundResponseRegistry registry,
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
        _db = db;
        _chainBuilder = chainBuilder;
        _scopeFactory = scopeFactory;
        _registry = registry;
        _apiKeyContext = apiKeyContext;
    }

    public async Task<IResult> HandleCreateAsync(HttpRequest httpRequest, HttpResponse httpResponse, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(httpRequest.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);

        ResponseCreateRequest? request;
        try { request = JsonSerializer.Deserialize<ResponseCreateRequest>(body); }
        catch (JsonException ex) { return Results.BadRequest(new { error = new { message = $"Invalid JSON: {ex.Message}" } }); }

        if (request is null || string.IsNullOrEmpty(request.Model))
            return Results.BadRequest(new { error = new { message = "`model` is required." } });

        if (request.Input.Any(i => i.Kind == ResponseInputItemKind.Unsupported))
            return Results.BadRequest(new { error = new { message = "One or more `input` items use an unsupported type. Only message, function_call, and function_call_output items are supported." } });

        if (request.Tools is { Count: > 0 } && request.Tools.Any(t => t.Type != "function"))
            return Results.BadRequest(new { error = new { message = "Only \"function\" tools are supported on a llama.cpp backend (built-in tools like web_search are not available)." } });

        bool store = request.Store ?? true;
        if (request.Background && !store)
            return Results.BadRequest(new { error = new { message = "`background: true` requires `store: true` — a background response can only be polled/cancelled if it's persisted." } });

        var responseId = $"resp_{Guid.NewGuid():N}";
        var createdAt = DateTimeOffset.UtcNow;

        Guid logId = Guid.Empty;
        try { logId = await _requestLogger.LogIncomingAsync(ApiProtocol.OpenAI, "/v1/responses", body, request.Model); } catch { /* logging failure shouldn't block the request */ }

        List<ChatMessage> messages;
        try { messages = await _chainBuilder.BuildMessagesAsync(request.PreviousResponseId, request.Instructions, request.Input, cancellationToken); }
        catch (Exception ex) { return Results.BadRequest(new { error = new { message = ex.Message } }); }

        if (messages.Count == 0)
            return Results.BadRequest(new { error = new { message = "`input` (or a valid `previous_response_id` conversation) is required." } });

        var preset = _presetManager.GetAllPresets().FirstOrDefault(p => p.Name == request.Model);

        // Reject models this API key isn't scoped to before touching the routing engine —
        // RoutingEngine's round-robin fallback would otherwise happily route an unresolved
        // model name to any healthy server, silently bypassing the scoping.
        if (preset is not null && !_apiKeyContext.IsModelAllowed(preset.Id))
            return Results.Json(new { error = new { message = $"Model '{request.Model}' is not accessible with this API key." } }, statusCode: 403);

        // Background + non-streaming: fire-and-forget, return "queued" immediately so the caller
        // can poll GET /v1/responses/{id} or cancel it — llama.cpp itself is always synchronous
        // per-request, so this is emulated entirely in the router.
        if (request.Background && !request.Stream)
        {
            _db.StoredResponses.Add(BuildStoredResponseRow(responseId, createdAt, request, "queued"));
            await _db.SaveChangesAsync(cancellationToken);

            var bgChatRequest = BuildChatCompletionRequest(request, messages, stream: false);
            var bgRouteRequest = BuildRouteRequest(request.Model, preset?.Id, bgChatRequest);

            var cts = new CancellationTokenSource();
            if (_gatewaySettings.BackendTimeoutSeconds > 0)
                cts.CancelAfter(TimeSpan.FromSeconds(_gatewaySettings.BackendTimeoutSeconds));
            _registry.Register(responseId, cts);

            _ = Task.Run(() => ProcessBackgroundAsync(responseId, bgRouteRequest, cts), CancellationToken.None);

            return Results.Json(BuildResponseObject(responseId, createdAt, request, "queued", new List<ResponseOutputItem>(), null), statusCode: 200);
        }

        var chatRequest = BuildChatCompletionRequest(request, messages, stream: request.Stream);
        var routeRequest = BuildRouteRequest(request.Model, preset?.Id, chatRequest);

        if (logId != Guid.Empty) { try { await _requestLogger.LogTranslatedPayloadAsync(logId, routeRequest.Payload); } catch { } }

        // Linked to the client's token so a client disconnect aborts the in-flight call to
        // llama.cpp immediately, plus an independent backend timeout so a hung backend still
        // gets cut off even while the client stays connected. (Not used for request.Background
        // == true — that path is intentionally decoupled from this request's lifetime; see the
        // separate CancellationTokenSource registered with _registry above.)
        using var backendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_gatewaySettings.BackendTimeoutSeconds > 0)
            backendCts.CancelAfter(TimeSpan.FromSeconds(_gatewaySettings.BackendTimeoutSeconds));
        var backendToken = backendCts.Token;

        var server = await _routingEngine.RouteAsync(routeRequest, cancellationToken);

        if (server is not null && request.Stream)
        {
            // background + stream: processed inline (no true reconnect/replay support — out of
            // scope), but still registered for /cancel and persisted up front so GET works
            // concurrently and after the fact.
            if (request.Background) _registry.Register(responseId, backendCts);
            return await HandleStreamingAsync(httpResponse, server, routeRequest, request, responseId, createdAt, preset, store, logId, backendToken, cancellationToken);
        }

        if (server is not null)
        {
            var routeResponse = await ProcessOnServerAsync(server, routeRequest, preset, logId, backendToken);
            var outputItems = BuildOutputItems(routeResponse);
            if (store)
            {
                _db.StoredResponses.Add(BuildStoredResponseRow(responseId, createdAt, request, "completed", outputItems, routeResponse));
                await _db.SaveChangesAsync(cancellationToken);
            }
            return Results.Json(BuildResponseObject(responseId, createdAt, request, "completed", outputItems, BuildUsage(routeResponse)));
        }

        // No server immediately available — queue, mirroring OpenAiHandler/ClaudeHandler. This
        // blocks until a response is ready even if `stream`/`background` was requested, same as
        // the existing protocol handlers do today.
        var queuedRouteResponse = await _queue.EnqueueAsync(routeRequest, backendToken);
        var queuedOutputItems = BuildOutputItems(queuedRouteResponse);
        if (store)
        {
            _db.StoredResponses.Add(BuildStoredResponseRow(responseId, createdAt, request, "completed", queuedOutputItems, queuedRouteResponse));
            await _db.SaveChangesAsync(cancellationToken);
        }
        if (logId != Guid.Empty)
        {
            try { await _requestLogger.LogCompletionAsync(logId, null, preset, queuedRouteResponse, 200, "Queued (Responses API)", false, true); } catch { }
        }
        return Results.Json(BuildResponseObject(responseId, createdAt, request, "completed", queuedOutputItems, BuildUsage(queuedRouteResponse)));
    }

    public async Task<IResult> HandleRetrieveAsync(string id, CancellationToken cancellationToken)
    {
        var row = await _db.StoredResponses.FindAsync(new object?[] { id }, cancellationToken);
        if (row is null) return Results.NotFound(new { error = new { message = $"No response found with id '{id}'." } });
        return Results.Json(RehydrateResponseObject(row));
    }

    public async Task<IResult> HandleDeleteAsync(string id, CancellationToken cancellationToken)
    {
        var row = await _db.StoredResponses.FindAsync(new object?[] { id }, cancellationToken);
        if (row is null) return Results.NotFound(new { error = new { message = $"No response found with id '{id}'." } });

        _db.StoredResponses.Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
        return Results.Json(new { id, @object = "response.deleted", deleted = true });
    }

    public async Task<IResult> HandleCancelAsync(string id, CancellationToken cancellationToken)
    {
        var cancelled = _registry.TryCancel(id);
        var row = await _db.StoredResponses.FindAsync(new object?[] { id }, cancellationToken);

        if (!cancelled && (row is null || TerminalStatuses.Contains(row.Status)))
            return Results.NotFound(new { error = new { message = $"No in-flight background response found with id '{id}'." } });

        if (row is not null && !TerminalStatuses.Contains(row.Status))
        {
            row.Status = "cancelled";
            await _db.SaveChangesAsync(cancellationToken);
        }

        return row is null ? Results.Json(new { id, status = "cancelled" }) : Results.Json(RehydrateResponseObject(row));
    }

    /// <summary>
    /// Runs a background (async) response to completion in its own DI scope — the HTTP request
    /// that started it (and its scoped services) is long gone by the time this finishes.
    /// </summary>
    private async Task ProcessBackgroundAsync(string responseId, RouteRequest routeRequest, CancellationTokenSource cts)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        try
        {
            await UpdateStatusIfNotTerminalAsync(db, responseId, "in_progress", null, CancellationToken.None);

            var routingEngine = scope.ServiceProvider.GetRequiredService<IRoutingEngine>();
            var serverManager = scope.ServiceProvider.GetRequiredService<IServerManager>();
            var statisticsService = scope.ServiceProvider.GetRequiredService<IStatisticsService>();
            var presetManager = scope.ServiceProvider.GetRequiredService<IPresetManager>();

            var server = await routingEngine.RouteAsync(routeRequest, cts.Token);
            RouteResponse? routeResponse;
            if (server is not null)
            {
                var provider = serverManager.GetProvider(server.Id);
                if (provider is null) throw new InvalidOperationException($"No backend provider registered for instance {server.Name}");

                routeResponse = await provider.SendRequestAsync(routeRequest.Payload, routeRequest.Protocol, cts.Token);
                if (routeResponse is null) throw new InvalidOperationException("Backend returned no response.");

                try
                {
                    var preset = routeRequest.PresetId.HasValue ? presetManager.GetById(routeRequest.PresetId.Value) : null;
                    await statisticsService.RecordRequestAsync(server, preset, routeResponse);
                }
                catch { /* stats recording failure shouldn't fail the background job */ }
            }
            else
            {
                var queueService = scope.ServiceProvider.GetRequiredService<IRequestQueueService>();
                routeResponse = await queueService.EnqueueAsync(routeRequest, cts.Token);
            }

            var outputItems = BuildOutputItems(routeResponse);
            await PersistCompletedAsync(db, responseId, routeResponse, outputItems, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await UpdateStatusIfNotTerminalAsync(db, responseId, "cancelled", null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background response {ResponseId} failed", responseId);
            await UpdateStatusIfNotTerminalAsync(db, responseId, "failed", ex.Message, CancellationToken.None);
        }
        finally
        {
            _registry.Remove(responseId);
        }
    }

    private async Task<IResult> HandleStreamingAsync(
        HttpResponse httpResponse, ServerInstance server, RouteRequest routeRequest, ResponseCreateRequest request,
        string responseId, DateTimeOffset createdAt, ModelPreset? preset, bool store, Guid logId,
        CancellationToken backendToken, CancellationToken clientToken)
    {
        var provider = _serverManager.GetProvider(server.Id);
        if (provider is null)
            return Results.Problem($"No backend provider registered for instance {server.Name}", statusCode: 503);

        httpResponse.StatusCode = 200;
        httpResponse.Headers.ContentType = "text/event-stream";
        httpResponse.Headers.CacheControl = "no-cache";
        var bodyWriter = httpResponse.BodyWriter;
        int seq = 0;

        if (store)
        {
            _db.StoredResponses.Add(BuildStoredResponseRow(responseId, createdAt, request, "in_progress"));
            await _db.SaveChangesAsync(clientToken);
        }

        var startingSnapshot = BuildResponseObject(responseId, createdAt, request, "in_progress", new List<ResponseOutputItem>(), null);
        await WriteEventAsync(bodyWriter, new ResponseStreamEvent { Type = "response.created", SequenceNumber = seq++, Response = startingSnapshot }, clientToken);
        await WriteEventAsync(bodyWriter, new ResponseStreamEvent { Type = "response.in_progress", SequenceNumber = seq++, Response = startingSnapshot }, clientToken);

        var messageItemId = $"msg_{Guid.NewGuid():N}";
        var reasoningItemId = $"rs_{Guid.NewGuid():N}";
        bool messageItemOpened = false;
        bool reasoningItemOpened = false;
        var textSoFar = new StringBuilder();
        var reasoningSoFar = new StringBuilder();
        RouteResponse? finalRouteResponse = null;

        try
        {
            // Note: no early "if backendToken.IsCancellationRequested break" guard here — once
            // cancelled (client disconnect or backend timeout), the provider stops reading from
            // llama.cpp and yields exactly one more chunk: a synthetic final chunk carrying
            // whatever partial content/stats it captured. Breaking before that chunk is
            // processed would discard it and leave finalRouteResponse unset.
            await foreach (var chunk in provider.SendStreamRequestAsync(routeRequest.Payload, routeRequest.Protocol, backendToken))
            {
                if (chunk.IsFinal && chunk.Response is not null)
                {
                    finalRouteResponse = chunk.Response;
                    break;
                }

                if (!string.IsNullOrEmpty(chunk.ReasoningContentDelta))
                {
                    if (!reasoningItemOpened)
                    {
                        reasoningItemOpened = true;
                        await WriteEventAsync(bodyWriter, new ResponseStreamEvent
                        {
                            Type = "response.output_item.added",
                            SequenceNumber = seq++,
                            OutputIndex = 0,
                            Item = new ResponseOutputItem { Id = reasoningItemId, Type = "reasoning" }
                        }, clientToken);
                    }
                    reasoningSoFar.Append(chunk.ReasoningContentDelta);
                    await WriteEventAsync(bodyWriter, new ResponseStreamEvent
                    {
                        Type = "response.reasoning_text.delta",
                        SequenceNumber = seq++,
                        ItemId = reasoningItemId,
                        OutputIndex = 0,
                        ContentIndex = 0,
                        Delta = chunk.ReasoningContentDelta
                    }, clientToken);
                }

                if (!string.IsNullOrEmpty(chunk.TextDelta))
                {
                    if (!messageItemOpened)
                    {
                        messageItemOpened = true;
                        await WriteEventAsync(bodyWriter, new ResponseStreamEvent
                        {
                            Type = "response.output_item.added",
                            SequenceNumber = seq++,
                            OutputIndex = reasoningItemOpened ? 1 : 0,
                            Item = new ResponseOutputItem { Id = messageItemId, Type = "message", Role = "assistant" }
                        }, clientToken);
                    }
                    textSoFar.Append(chunk.TextDelta);
                    await WriteEventAsync(bodyWriter, new ResponseStreamEvent
                    {
                        Type = "response.output_text.delta",
                        SequenceNumber = seq++,
                        ItemId = messageItemId,
                        OutputIndex = reasoningItemOpened ? 1 : 0,
                        ContentIndex = 0,
                        Delta = chunk.TextDelta
                    }, clientToken);
                }

                if (chunk.ToolCallDeltas is { Count: > 0 })
                {
                    foreach (var delta in chunk.ToolCallDeltas)
                    {
                        await WriteEventAsync(bodyWriter, new ResponseStreamEvent
                        {
                            Type = "response.function_call_arguments.delta",
                            SequenceNumber = seq++,
                            ItemId = delta.Id,
                            Delta = delta.Function.Arguments
                        }, clientToken);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (store) { try { await UpdateStatusIfNotTerminalAsync(_db, responseId, "failed", ex.Message, CancellationToken.None); } catch { } }
            _registry.Remove(responseId);
            throw;
        }

        // Best-effort from here on — the client may already be gone (that's the same disconnect
        // that just cancelled the backend call above). Don't let a failed write skip persistence,
        // stats recording, or logging below.
        try
        {
            if (reasoningItemOpened)
            {
                await WriteEventAsync(bodyWriter, new ResponseStreamEvent
                {
                    Type = "response.reasoning_text.done", SequenceNumber = seq++, ItemId = reasoningItemId, OutputIndex = 0, ContentIndex = 0, Text = reasoningSoFar.ToString()
                }, clientToken);
            }
            if (messageItemOpened)
            {
                await WriteEventAsync(bodyWriter, new ResponseStreamEvent
                {
                    Type = "response.output_text.done", SequenceNumber = seq++, ItemId = messageItemId, OutputIndex = reasoningItemOpened ? 1 : 0, ContentIndex = 0, Text = textSoFar.ToString()
                }, clientToken);
            }
        }
        catch (OperationCanceledException) { }

        var routeResponse = finalRouteResponse ?? new RouteResponse { Payload = textSoFar.ToString(), ReasoningContent = reasoningSoFar.Length > 0 ? reasoningSoFar.ToString() : null };
        var outputItems = BuildOutputItems(routeResponse);
        var usage = BuildUsage(routeResponse);

        try
        {
            foreach (var item in outputItems)
            {
                await WriteEventAsync(bodyWriter, new ResponseStreamEvent { Type = "response.output_item.done", SequenceNumber = seq++, Item = item }, clientToken);
            }

            var completedResponse = BuildResponseObject(responseId, createdAt, request, "completed", outputItems, usage);
            await WriteEventAsync(bodyWriter, new ResponseStreamEvent { Type = "response.completed", SequenceNumber = seq++, Response = completedResponse }, clientToken);
        }
        catch (OperationCanceledException) { }

        if (store) await PersistCompletedAsync(_db, responseId, routeResponse, outputItems, CancellationToken.None);
        if (request.Background) _registry.Remove(responseId);

        try
        {
            var presetIdForStats = routeRequest.PresetId ?? server.ActivePresetId;
            var presetForStats = presetIdForStats.HasValue ? _presetManager.GetById(presetIdForStats.Value) : null;
            await _statisticsService.RecordRequestAsync(server, presetForStats, routeResponse);
        }
        catch { /* stats recording failure shouldn't block the response */ }

        if (logId != Guid.Empty)
        {
            try { await _requestLogger.LogCompletionAsync(logId, server, preset, routeResponse, 200, $"Streamed (Responses API) {routeResponse.GeneratedTokenCount} tokens", true, false); } catch { }
        }

        return Results.Empty;
    }

    private async Task<RouteResponse> ProcessOnServerAsync(ServerInstance server, RouteRequest routeRequest, ModelPreset? preset, Guid logId, CancellationToken cancellationToken)
    {
        var provider = _serverManager.GetProvider(server.Id);
        if (provider is null)
            throw new InvalidOperationException($"No backend provider registered for instance {server.Name}");

        var response = await provider.SendRequestAsync(routeRequest.Payload, routeRequest.Protocol, cancellationToken);
        if (response is null)
            throw new InvalidOperationException($"Backend returned no response from server {server.Name}");

        try { await _statisticsService.RecordRequestAsync(server, preset, response); } catch { }

        if (logId != Guid.Empty)
        {
            try { await _requestLogger.LogCompletionAsync(logId, server, preset, response, 200, $"Non-streaming (Responses API): {response.GeneratedTokenCount} tokens", false, false); } catch { }
        }

        return response;
    }

    private static async Task WriteEventAsync(PipeWriter bodyWriter, ResponseStreamEvent evt, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(evt);
        var frame = $"event: {evt.Type}\ndata: {json}\n\n";
        await bodyWriter.WriteAsync(Encoding.UTF8.GetBytes(frame), ct);
        await bodyWriter.FlushAsync(ct);
    }

    private static async Task<StoredResponse?> UpdateStatusIfNotTerminalAsync(LRDbContext db, string responseId, string status, string? errorMessage, CancellationToken ct)
    {
        var row = await db.StoredResponses.FindAsync(new object?[] { responseId }, ct);
        if (row is null || TerminalStatuses.Contains(row.Status)) return row;
        row.Status = status;
        if (errorMessage is not null) row.ErrorMessage = errorMessage;
        await db.SaveChangesAsync(ct);
        return row;
    }

    private static async Task PersistCompletedAsync(LRDbContext db, string responseId, RouteResponse routeResponse, List<ResponseOutputItem> outputItems, CancellationToken ct)
    {
        var row = await db.StoredResponses.FindAsync(new object?[] { responseId }, ct);
        if (row is null || TerminalStatuses.Contains(row.Status)) return; // store:false, or already cancelled/failed by a race
        row.Status = "completed";
        row.OwnOutputItemsJson = ResponseChainBuilder.SerializeOutputItems(outputItems);
        row.InputTokens = routeResponse.PromptTokensProcessed;
        row.OutputTokens = routeResponse.GeneratedTokenCount;
        await db.SaveChangesAsync(ct);
    }

    private static StoredResponse BuildStoredResponseRow(
        string id, DateTimeOffset createdAt, ResponseCreateRequest request, string status,
        List<ResponseOutputItem>? outputItems = null, RouteResponse? routeResponse = null) => new()
    {
        Id = id,
        CreatedAt = createdAt,
        PreviousResponseId = request.PreviousResponseId,
        Model = request.Model,
        Instructions = request.Instructions,
        OwnInputItemsJson = ResponseChainBuilder.SerializeInputItems(request.Input),
        OwnOutputItemsJson = outputItems is null ? "[]" : ResponseChainBuilder.SerializeOutputItems(outputItems),
        Status = status,
        Store = true,
        Background = request.Background,
        InputTokens = routeResponse?.PromptTokensProcessed ?? 0,
        OutputTokens = routeResponse?.GeneratedTokenCount ?? 0,
        ToolsJson = request.Tools is null ? null : JsonSerializer.Serialize(request.Tools),
        ToolChoiceJson = request.ToolChoice is null ? null : JsonSerializer.Serialize(request.ToolChoice),
        MetadataJson = request.Metadata is null ? null : JsonSerializer.Serialize(request.Metadata)
    };

    private ResponseObject RehydrateResponseObject(StoredResponse row) => new()
    {
        Id = row.Id,
        CreatedAt = row.CreatedAt.ToUnixTimeSeconds(),
        Status = row.Status,
        Error = row.ErrorMessage is null ? null : new ResponseError { Code = "server_error", Message = row.ErrorMessage },
        Instructions = row.Instructions,
        Model = row.Model,
        Output = ResponseChainBuilder.DeserializeOutputItems(row.OwnOutputItemsJson),
        Usage = new ResponseUsage { InputTokens = row.InputTokens, OutputTokens = row.OutputTokens, TotalTokens = row.InputTokens + row.OutputTokens },
        PreviousResponseId = row.PreviousResponseId,
        Store = row.Store,
        Background = row.Background,
        Tools = row.ToolsJson is null ? new List<ResponseTool>() : JsonSerializer.Deserialize<List<ResponseTool>>(row.ToolsJson) ?? new List<ResponseTool>(),
        ToolChoice = row.ToolChoiceJson is null ? null : JsonSerializer.Deserialize<ChatToolChoice>(row.ToolChoiceJson),
        Metadata = row.MetadataJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(row.MetadataJson)
    };

    private static List<ResponseOutputItem> BuildOutputItems(RouteResponse response)
    {
        var items = new List<ResponseOutputItem>();

        if (!string.IsNullOrEmpty(response.ReasoningContent))
        {
            items.Add(new ResponseOutputItem
            {
                Id = $"rs_{Guid.NewGuid():N}",
                Type = "reasoning",
                Content = new List<ResponseOutputContentPart> { new() { Type = "reasoning_text", Text = response.ReasoningContent } }
            });
        }

        if (!string.IsNullOrEmpty(response.Payload))
        {
            items.Add(new ResponseOutputItem
            {
                Id = $"msg_{Guid.NewGuid():N}",
                Type = "message",
                Role = "assistant",
                Status = "completed",
                Content = new List<ResponseOutputContentPart> { new() { Type = "output_text", Text = response.Payload } }
            });
        }

        if (response.ToolCalls is { Count: > 0 })
        {
            foreach (var call in response.ToolCalls)
            {
                items.Add(new ResponseOutputItem
                {
                    Id = $"fc_{Guid.NewGuid():N}",
                    Type = "function_call",
                    Status = "completed",
                    CallId = call.Id,
                    Name = call.Function.Name,
                    Arguments = call.Function.Arguments
                });
            }
        }

        return items;
    }

    private static ResponseUsage BuildUsage(RouteResponse response) => new()
    {
        InputTokens = response.PromptTokensProcessed,
        OutputTokens = response.GeneratedTokenCount,
        TotalTokens = response.PromptTokensProcessed + response.GeneratedTokenCount
    };

    private static ResponseObject BuildResponseObject(
        string id, DateTimeOffset createdAt, ResponseCreateRequest request, string status,
        List<ResponseOutputItem> output, ResponseUsage? usage) => new()
    {
        Id = id,
        CreatedAt = createdAt.ToUnixTimeSeconds(),
        Status = status,
        Instructions = request.Instructions,
        Model = request.Model,
        Output = output,
        Usage = usage,
        ParallelToolCalls = request.ParallelToolCalls ?? true,
        PreviousResponseId = request.PreviousResponseId,
        Store = request.Store ?? true,
        Background = request.Background,
        Temperature = request.Temperature,
        TopP = request.TopP,
        ToolChoice = request.ToolChoice,
        Tools = request.Tools ?? new List<ResponseTool>(),
        Truncation = request.Truncation,
        Metadata = request.Metadata
    };

    private static ChatCompletionRequest BuildChatCompletionRequest(ResponseCreateRequest request, List<ChatMessage> messages, bool stream)
    {
        var chatRequest = new ChatCompletionRequest
        {
            Model = request.Model,
            Messages = messages,
            Stream = stream,
            Temperature = request.Temperature,
            TopP = request.TopP,
            MaxCompletionTokens = request.MaxOutputTokens,
            ParallelToolCalls = request.ParallelToolCalls,
            ToolChoice = request.ToolChoice,
            Tools = request.Tools?.Select(t => t.ToChatTool()).ToList(),
            ResponseFormat = request.Text?.Format
        };

        // llama.cpp (like OpenAI) only includes token usage in the SSE stream when
        // stream_options.include_usage is set — force it on so usage is always available.
        if (stream)
            chatRequest.StreamOptions = new StreamOptions { IncludeUsage = true };

        return chatRequest;
    }

    private static RouteRequest BuildRouteRequest(string model, Guid? presetId, ChatCompletionRequest chatRequest) => new()
    {
        ModelName = model,
        PresetId = presetId,
        // Omit null fields — backend rejects "name":null etc.
        Payload = JsonSerializer.Serialize(chatRequest, BackendJsonOpts)
    };
}
