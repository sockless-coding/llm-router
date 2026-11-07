using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Pages.Api;

/// <summary>
/// Shared embeddings pipeline for every protocol handler (OpenAI <c>/v1/embeddings</c>, Ollama
/// <c>/api/embed</c>): forwards an OpenAI-shaped embeddings payload verbatim to the backend's
/// <c>/v1/embeddings</c> (see <see cref="PassthroughGateway"/>).
/// The target preset must be launched with embeddings enabled (and a pooling type other than
/// <c>none</c>) — llama.cpp rejects the call otherwise, and that error is passed back as-is.
/// </summary>
public class EmbeddingsGateway(
    ILogger<EmbeddingsGateway> logger,
    IServerManager serverManager,
    IPresetManager presetManager,
    IRoutingEngine routingEngine,
    IRequestQueueService queue,
    IApiRequestLogger requestLogger,
    GatewaySettings gatewaySettings,
    IApiKeyRequestContext apiKeyContext)
    : PassthroughGateway(logger, serverManager, presetManager, routingEngine, queue, requestLogger, gatewaySettings, apiKeyContext)
{
    private const string BackendEndpoint = "/v1/embeddings";

    /// <param name="protocol">The client-facing protocol, for the request log.</param>
    /// <param name="endpointPath">The client-facing path, for the request log.</param>
    /// <param name="incomingBody">The client's original request body, for the request log.</param>
    /// <param name="model">The requested model (preset) name.</param>
    /// <param name="backendPayload">An OpenAI <c>/v1/embeddings</c> request body.</param>
    public async Task<PassthroughResult> CreateAsync(
        ApiProtocol protocol,
        string endpointPath,
        string incomingBody,
        string? model,
        string backendPayload,
        CancellationToken cancellationToken)
    {
        if (TryResolvePreset(model, out var preset) is { } rejected)
            return rejected;

        return await ForwardAsync(protocol, endpointPath, incomingBody, preset, BackendEndpoint, backendPayload,
            r => $"Embeddings: {r.PromptTokensProcessed} prompt tokens", cancellationToken);
    }
}
