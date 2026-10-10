using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;

namespace LR.Application.Pages.Api;

/// <summary>
/// The outcome of an embeddings call: either the backend's raw OpenAI-shaped
/// <c>/v1/embeddings</c> response, or an HTTP status + message for the protocol handler to
/// wrap in its own error shape.
/// </summary>
public sealed record EmbeddingsResult(RouteResponse? Response, int StatusCode, string? Error)
{
    public bool IsSuccess => Response is not null;
}

/// <summary>
/// Shared embeddings pipeline for every protocol handler (OpenAI <c>/v1/embeddings</c>, Ollama
/// <c>/api/embed</c>): resolves the preset, enforces API-key scoping, routes (starting or
/// swapping the preset's server if needed), falls back to the request queue, and forwards an
/// OpenAI-shaped embeddings payload verbatim to the backend's <c>/v1/embeddings</c>.
/// The target preset must be launched with embeddings enabled (and a pooling type other than
/// <c>none</c>) — llama.cpp rejects the call otherwise, and that error is passed back as-is.
/// </summary>
public class EmbeddingsGateway
{
    private const string BackendEndpoint = "/v1/embeddings";

    private readonly ILogger<EmbeddingsGateway> _logger;
    private readonly IServerManager _serverManager;
    private readonly IPresetManager _presetManager;
    private readonly IRoutingEngine _routingEngine;
    private readonly IRequestQueueService _queue;
    private readonly IApiRequestLogger _requestLogger;
    private readonly GatewaySettings _gatewaySettings;
    private readonly IApiKeyRequestContext _apiKeyContext;

    public EmbeddingsGateway(
        ILogger<EmbeddingsGateway> logger,
        IServerManager serverManager,
        IPresetManager presetManager,
        IRoutingEngine routingEngine,
        IRequestQueueService queue,
        IApiRequestLogger requestLogger,
        GatewaySettings gatewaySettings,
        IApiKeyRequestContext apiKeyContext)
    {
        _logger = logger;
        _serverManager = serverManager;
        _presetManager = presetManager;
        _routingEngine = routingEngine;
        _queue = queue;
        _requestLogger = requestLogger;
        _gatewaySettings = gatewaySettings;
        _apiKeyContext = apiKeyContext;
    }

    /// <param name="protocol">The client-facing protocol, for the request log.</param>
    /// <param name="endpointPath">The client-facing path, for the request log.</param>
    /// <param name="incomingBody">The client's original request body, for the request log.</param>
    /// <param name="model">The requested model (preset) name.</param>
    /// <param name="backendPayload">An OpenAI <c>/v1/embeddings</c> request body.</param>
    public async Task<EmbeddingsResult> CreateAsync(
        ApiProtocol protocol,
        string endpointPath,
        string incomingBody,
        string? model,
        string backendPayload,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
            return new EmbeddingsResult(null, 400, "Missing 'model' in request body.");

        // Unlike chat, an unknown model is rejected outright rather than handed to the routing
        // engine's round-robin fallback: any running chat model would "answer", but its vectors
        // would be meaningless to a client expecting the model it named.
        var preset = _presetManager.FindByModelName(model);
        if (preset is null)
            return new EmbeddingsResult(null, 404, $"Model '{model}' not found.");
        if (!_apiKeyContext.IsModelAllowed(preset.Id))
            return new EmbeddingsResult(null, 403, $"Model '{model}' is not accessible with this API key.");

        var routeRequest = new RouteRequest
        {
            ModelName = model,
            PresetId = preset.Id,
            ApiKeyId = _apiKeyContext.CurrentKey?.Id,
            Payload = backendPayload,
            BackendEndpoint = BackendEndpoint
        };

        Guid logId = Guid.Empty;
        try { logId = await _requestLogger.LogIncomingAsync(protocol, endpointPath, incomingBody, model); }
        catch { /* Logging failure shouldn't block the request */ }
        if (logId != Guid.Empty) { try { await _requestLogger.LogTranslatedPayloadAsync(logId, backendPayload); } catch { } }

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
                    response = await provider.SendRawRequestAsync(BackendEndpoint, backendPayload, backendCts.Token);
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
                    await _requestLogger.LogBackendResponseAsync(logId, response.Payload);
                    await _requestLogger.LogCompletionAsync(logId, server, preset, response, 200,
                        $"Embeddings: {response.PromptTokensProcessed} prompt tokens", false, wasQueued);
                }
                catch { /* Logging failure shouldn't block the response */ }
            }

            return new EmbeddingsResult(response, 200, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var status = ex switch
            {
                TimeoutException or OperationCanceledException => 504,
                HttpRequestException { StatusCode: { } code } when (int)code is >= 400 and < 500 => (int)code,
                _ => 502
            };
            var message = BackendErrorClassifier.ToClientMessage(ex);
            _logger.LogWarning(ex, "Embeddings request failed for model {Model}", model);
            if (logId != Guid.Empty) { try { await _requestLogger.LogErrorAsync(logId, message, status); } catch { } }

            return new EmbeddingsResult(null, status, message);
        }
    }
}
