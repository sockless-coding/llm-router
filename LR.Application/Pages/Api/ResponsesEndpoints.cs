using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace LR.Application.Pages.Api;

/// <summary>
/// Extension methods for registering OpenAI Responses API endpoints.
/// </summary>
public static class ResponsesEndpoints
{
    /// <summary>
    /// Maps the Responses API endpoints:
    /// - POST   /v1/responses
    /// - GET    /v1/responses/{id}
    /// - DELETE /v1/responses/{id}
    /// - POST   /v1/responses/{id}/cancel
    /// </summary>
    public static IEndpointRouteBuilder MapResponsesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/responses", async (
                ResponsesHandler handler,
                HttpRequest httpRequest,
                HttpResponse httpResponse,
                CancellationToken ct) =>
            {
                return await handler.HandleCreateAsync(httpRequest, httpResponse, ct);
            });

        app.MapGet("/v1/responses/{id}", async (
                ResponsesHandler handler,
                string id,
                CancellationToken ct) =>
            {
                return await handler.HandleRetrieveAsync(id, ct);
            });

        app.MapDelete("/v1/responses/{id}", async (
                ResponsesHandler handler,
                string id,
                CancellationToken ct) =>
            {
                return await handler.HandleDeleteAsync(id, ct);
            });

        app.MapPost("/v1/responses/{id}/cancel", async (
                ResponsesHandler handler,
                string id,
                CancellationToken ct) =>
            {
                return await handler.HandleCancelAsync(id, ct);
            });

        return app;
    }
}
