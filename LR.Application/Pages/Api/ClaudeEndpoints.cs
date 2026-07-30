using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace LR.Application.Pages.Api;

/// <summary>
/// Extension methods for registering Claude-compatible API endpoints.
/// </summary>
public static class ClaudeEndpoints
{
    /// <summary>
    /// Maps Claude-compatible endpoints:
    /// - POST /v1/messages
    /// </summary>
    public static IEndpointRouteBuilder MapClaudeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/messages", async (
                ClaudeHandler handler,
                HttpRequest httpRequest,
                HttpResponse httpResponse,
                CancellationToken ct) =>
            {
                return await handler.HandleChatCompletionAsync(httpRequest, httpResponse, ct);
            });

        return app;
    }
}
