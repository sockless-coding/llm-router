using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;

namespace LR.Application.Pages.Api;

/// <summary>
/// The outcome of a pass-through call: either the backend's raw JSON response, or an HTTP status
/// + message for the protocol handler to wrap in its own error shape.
/// </summary>
public sealed record PassthroughResult(RouteResponse? Response, int StatusCode, string? Error)
{
    public bool IsSuccess => Response is not null;
}

/// <summary>
/// Shared pipeline for endpoints whose client body is already in llama-server's own shape
/// (<c>/v1/embeddings</c>, <c>/v1/systemone</c>): enforces API-key scoping, routes (starting or
/// swapping the preset's server if needed), falls back to the request queue, forwards the payload
/// verbatim to the backend endpoint, and logs the call.
/// </summary>
public abstract class PassthroughGateway
{
    private readonly ILogger _logger;
    private readonly IServerManager _serverManager;
    private readonly IRoutingEngine _routingEngine;
    private readonly IRequestQueueService _queue;
    private readonly IApiRequestLogger _requestLogger;
    private readonly GatewaySettings _gatewaySettings;

    protected PassthroughGateway(
        ILogger logger,
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
        PresetManager = presetManager;
        _routingEngine = routingEngine;
        _queue = queue;
        _requestLogger = requestLogger;
        _gatewaySettings = gatewaySettings;
        ApiKeyContext = apiKeyContext;
    }

    protected IPresetManager PresetManager { get; }
    protected IApiKeyRequestContext ApiKeyContext { get; }

    /// <summary>
    /// Resolves <paramref name="model"/> to a preset the caller's key may use. Unlike chat, an
    /// unknown model is rejected outright rather than handed to the routing engine's round-robin
    /// fallback: any running model would "answer", but not as the model the client named.
    /// </summary>
    protected PassthroughResult? TryResolvePreset(string? model, out ModelPreset preset)
    {
        preset = null!;
        if (string.IsNullOrWhiteSpace(model))
            return new PassthroughResult(null, 400, "Missing 'model' in request body.");
        var found = PresetManager.FindByModelName(model);
        if (found is null)
            return new PassthroughResult(null, 404, $"Model '{model}' not found.");
        if (!ApiKeyContext.IsModelAllowed(found.Id))
            return new PassthroughResult(null, 403, $"Model '{model}' is not accessible with this API key.");
        preset = found;
        return null;
    }

    /// <param name="protocol">The client-facing protocol, for the request log.</param>
    /// <param name="endpointPath">The client-facing path, for the request log.</param>
    /// <param name="incomingBody">The client's original request body, for the request log.</param>
    /// <param name="preset">The resolved target preset (see <see cref="TryResolvePreset"/>).</param>
    /// <param name="backendEndpoint">The llama-server endpoint <paramref name="backendPayload"/> is posted to.</param>
    /// <param name="summary">The request log's one-line summary of a successful response.</param>
    protected async Task<PassthroughResult> ForwardAsync(
        ApiProtocol protocol,
        string endpointPath,
        string incomingBody,
        ModelPreset preset,
        string backendEndpoint,
        string backendPayload,
        Func<RouteResponse, string> summary,
        CancellationToken cancellationToken)
    {
        var routeRequest = new RouteRequest
        {
            ModelName = preset.Name,
            PresetId = preset.Id,
            ApiKeyId = ApiKeyContext.CurrentKey?.Id,
            Payload = backendPayload,
            BackendEndpoint = backendEndpoint
        };

        Guid logId = Guid.Empty;
        try { logId = await _requestLogger.LogIncomingAsync(protocol, endpointPath, incomingBody, preset.Name); }
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
                    response = await provider.SendRawRequestAsync(backendEndpoint, backendPayload, backendCts.Token);
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
                    await _requestLogger.LogCompletionAsync(logId, server, preset, response, 200, summary(response), false, wasQueued);
                }
                catch { /* Logging failure shouldn't block the response */ }
            }

            return new PassthroughResult(response, 200, null);
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
            var message = ex is ModelModalityMismatchException ? ex.Message : BackendErrorClassifier.ToClientMessage(ex);
            _logger.LogWarning(ex, "{Endpoint} request failed for model {Model}", endpointPath, preset.Name);
            if (logId != Guid.Empty) { try { await _requestLogger.LogErrorAsync(logId, message, status); } catch { } }

            return new PassthroughResult(null, status, message);
        }
    }
}
