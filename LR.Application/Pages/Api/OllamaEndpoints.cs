using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace LR.Application.Pages.Api;

/// <summary>
/// Extension methods for registering Ollama-compatible API endpoints.
/// </summary>
public static class OllamaEndpoints
{
    /// <summary>
    /// Maps Ollama-compatible endpoints:
    /// - POST /api/chat
    /// - GET  /api/tags
    /// - POST /api/show
    /// </summary>
    public static IEndpointRouteBuilder MapOllamaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/chat", async (
                OllamaHandler handler,
                HttpRequest httpRequest,
                HttpResponse httpResponse,
                CancellationToken ct) =>
            {
                return await handler.HandleChatCompletionAsync(httpRequest, httpResponse, ct);
            });

        app.MapGet("/api/tags", async (
                OllamaHandler handler) =>
            {
                var result = await handler.HandleListModelsAsync();
                return Results.Json(result);
            });

        app.MapPost("/api/show", async (OllamaHandler handler, HttpRequest httpRequest) =>
        {
            using var reader = new StreamReader(httpRequest.Body);
            var body = await reader.ReadToEndAsync();
            var request = System.Text.Json.JsonSerializer.Deserialize<LR.Core.Models.Ollama.ShowRequest>(body);

            // Ollama uses either "name" or "model" field in the request body
            var modelName = request?.Name ?? request?.Model;
            if (string.IsNullOrWhiteSpace(modelName))
                return Results.BadRequest("Missing 'name' or 'model' in request body");

            var result = await handler.HandleShowModelAsync(modelName);
            // Handle case where the model wasn't found and result is an IResult
            if (result is Microsoft.AspNetCore.Http.IResult iResult)
                return iResult;

            return Results.Json(result!);
        });

        return app;
    }
}
