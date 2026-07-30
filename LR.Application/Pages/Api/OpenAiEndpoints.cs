using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace LR.Application.Pages.Api;

/// <summary>
/// Extension methods for registering OpenAI-compatible API endpoints.
/// </summary>
public static class OpenAiEndpoints
{
    /// <summary>
    /// Maps OpenAI-compatible endpoints:
    /// - POST /v1/chat/completions
    /// - GET  /v1/models
    /// </summary>
    public static IEndpointRouteBuilder MapOpenAiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/chat/completions", async (
                OpenAiHandler handler,
                HttpRequest httpRequest,
                HttpResponse httpResponse,
                CancellationToken ct) =>
            {
                return await handler.HandleChatCompletionAsync(httpRequest, httpResponse, ct);
            });

        app.MapGet("/v1/models", async (
                OpenAiHandler handler) =>
            {
                var result = await handler.HandleListModelsAsync();
                return Results.Json(result);
            });

        return app;
    }
}
