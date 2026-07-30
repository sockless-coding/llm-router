using LR.Core.Models;

namespace LR.Application.Pages.Api;

/// <summary>
/// Handles a specific API protocol (OpenAI, Claude, Ollama).
/// Converts between the protocol-specific format and internal RouteRequest/RouteResponse.
/// </summary>
public interface IProtocolHandler
{
    /// <summary>
    /// The protocol this handler supports.
    /// </summary>
    ApiProtocol Protocol { get; }

    /// <summary>
    /// The path prefix for this protocol's endpoints (e.g., "/v1" for OpenAI, "/api" for Ollama).
    /// </summary>
    string PathPrefix { get; }

    /// <summary>
    /// Handle a chat completion request (streaming or non-streaming based on request body).
    /// Returns IResult for flexible JSON or streaming responses.
    /// </summary>
    Task<IResult> HandleChatCompletionAsync(HttpRequest httpRequest, HttpResponse httpResponse, CancellationToken cancellationToken);

    /// <summary>
    /// Handle a list models request. Returns the model list response.
    /// </summary>
    Task<object> HandleListModelsAsync();
}

