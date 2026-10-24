using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;

namespace LR.Application.Pages.Api;

/// <summary>
/// <c>POST /v1/images/generations</c>: resolves the preset, enforces API-key scoping, routes (starting
/// or swapping the preset's image server if needed — the same routing, fallback, memory-group and
/// queueing as chat), and forwards the OpenAI-shaped body to the backend's own
/// <c>/v1/images/generations</c>. The engine's provider translates engine-specific extras on the way
/// (see <c>StableDiffusionCppProvider.ToSdCppImageRequest</c>); the response (<c>data[].b64_json</c>)
/// is returned verbatim. Only presets on image engines (<see cref="EngineModality.Image"/>) qualify.
/// </summary>
public class ImagesGateway
{
    public const string GenerationsPath = "/v1/images/generations";

    private readonly ILogger<ImagesGateway> _logger;
    private readonly IServerManager _serverManager;
    private readonly IPresetManager _presetManager;
    private readonly IRoutingEngine _routingEngine;
    private readonly IRequestQueueService _queue;
    private readonly IApiRequestLogger _requestLogger;
    private readonly GatewaySettings _gatewaySettings;
    private readonly IApiKeyRequestContext _apiKeyContext;
    private readonly IEngineCatalog _engines;

    public ImagesGateway(
        ILogger<ImagesGateway> logger,
        IServerManager serverManager,
        IPresetManager presetManager,
        IRoutingEngine routingEngine,
        IRequestQueueService queue,
        IApiRequestLogger requestLogger,
        GatewaySettings gatewaySettings,
        IApiKeyRequestContext apiKeyContext,
        IEngineCatalog engines)
    {
        _logger = logger;
        _serverManager = serverManager;
        _presetManager = presetManager;
        _routingEngine = routingEngine;
        _queue = queue;
        _requestLogger = requestLogger;
        _gatewaySettings = gatewaySettings;
        _apiKeyContext = apiKeyContext;
        _engines = engines;
    }

    public async Task<IResult> HandleGenerationsAsync(HttpRequest httpRequest, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(httpRequest.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);

        string? model;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("prompt", out var prompt) || prompt.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(prompt.GetString()))
                return Error(400, "Missing 'prompt' in request body.");
            model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        }
        catch (JsonException)
        {
            return Error(400, "Invalid JSON in request body.");
        }

        if (string.IsNullOrWhiteSpace(model))
            return Error(400, "Missing 'model' in request body.");

        // Like embeddings, an unknown model is rejected outright: the round-robin fallback would
        // hand it to whatever image model happens to be loaded, which isn't what the client named.
        var preset = _presetManager.FindByModelName(model);
        if (preset is null)
            return Error(404, $"Model '{model}' not found.");
        if (!_apiKeyContext.IsModelAllowed(preset.Id))
            return Error(403, $"Model '{model}' is not accessible with this API key.");

        var owner = _serverManager.GetAllInstances().FirstOrDefault(s => s.Id == preset.ServerInstanceId);
        if (owner is null || _engines.ModalityOf(owner.Engine) != EngineModality.Image)
            return Error(400, $"Model '{model}' is not an image model.");

        var routeRequest = new RouteRequest
        {
            ModelName = model,
            PresetId = preset.Id,
            ApiKeyId = _apiKeyContext.CurrentKey?.Id,
            Modality = EngineModality.Image,
            Payload = body,
            BackendEndpoint = GenerationsPath,
        };

        Guid logId = Guid.Empty;
        try { logId = await _requestLogger.LogIncomingAsync(ApiProtocol.OpenAI, GenerationsPath, body, model); }
        catch { /* Logging failure shouldn't block the request */ }

        using var backendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_gatewaySettings.BackendTimeoutSeconds > 0)
            backendCts.CancelAfter(TimeSpan.FromSeconds(_gatewaySettings.BackendTimeoutSeconds));

        ServerInstance? server = null;
        bool wasQueued = false;
        try
        {
            RouteResponse? response;
            using (var decision = await _routingEngine.RouteAsync(routeRequest, cancellationToken))
            {
                server = decision?.Server;
                if (server is not null)
                {
                    var provider = _serverManager.GetProvider(server.Id)
                        ?? throw new InvalidOperationException($"No backend provider registered for instance {server.Name}");
                    response = await provider.SendRawRequestAsync(GenerationsPath, body, backendCts.Token);
                }
                else
                {
                    // Server busy or still (re)starting with this preset — wait in the queue.
                    wasQueued = true;
                    response = await _queue.EnqueueAsync(routeRequest, backendCts.Token);
                }
            }

            if (response is null)
                throw new InvalidOperationException("Backend returned no response.");

            if (logId != Guid.Empty)
            {
                try
                {
                    var (logged, count) = RedactImages(response.Payload);
                    await _requestLogger.LogBackendResponseAsync(logId, logged);
                    await _requestLogger.LogCompletionAsync(logId, server, preset, response, 200,
                        $"Images: {count} generated in {response.TotalLatencyMs / 1000:F1}s", false, wasQueued);
                }
                catch { /* Logging failure shouldn't block the response */ }
            }

            return Results.Content(response.Payload, "application/json");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var status = ex switch
            {
                ModelModalityMismatchException => 400,
                TimeoutException or OperationCanceledException => 504,
                HttpRequestException { StatusCode: { } code } when (int)code is >= 400 and < 500 => (int)code,
                _ => 502
            };
            var message = BackendErrorClassifier.ToClientMessage(ex);
            _logger.LogWarning(ex, "Image generation failed for model {Model}", model);
            if (logId != Guid.Empty) { try { await _requestLogger.LogErrorAsync(logId, message, status); } catch { } }

            return Error(status, message);
        }
    }

    /// <summary>
    /// The response with each image's base64 replaced by its size — a single image is megabytes of
    /// text that has no business in the request log — plus how many images it held.
    /// </summary>
    internal static (string? Logged, int Count) RedactImages(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
            return (payload, 0);
        try
        {
            if (JsonNode.Parse(payload) is not JsonObject root || root["data"] is not JsonArray data)
                return (payload, 0);
            foreach (var item in data.OfType<JsonObject>())
            {
                if (item["b64_json"] is JsonValue b64 && b64.TryGetValue<string>(out var s))
                    item["b64_json"] = $"<{s.Length * 3 / 4 / 1024} KB image omitted>";
            }
            return (root.ToJsonString(), data.Count);
        }
        catch (JsonException)
        {
            return (payload, 0);
        }
    }

    private static IResult Error(int statusCode, string message) =>
        Results.Json(new
        {
            error = new { message, type = statusCode == 404 ? "not_found_error" : "invalid_request_error" }
        }, statusCode: statusCode);
}
