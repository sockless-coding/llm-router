using System.Text.Json;

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
    /// - POST /api/generate
    /// - POST /api/embed
    /// - GET  /api/ps
    /// - GET  /api/version
    /// </summary>
    public static IEndpointRouteBuilder MapOllamaEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/version — reports the Ollama-compatible server version.
        // Required by some clients (e.g. VS Code Copilot Chat's Ollama provider) which gate
        // model discovery on a minimum server version before models reach the model picker.
        app.MapGet("/api/version", () => Results.Json(new { version = "0.9.0" }));

        // POST /api/chat — chat completions with messages array
        app.MapPost("/api/chat", async (
                OllamaHandler handler,
                HttpRequest httpRequest,
                HttpResponse httpResponse,
                CancellationToken ct) =>
            {
                return await handler.HandleChatCompletionAsync(httpRequest, httpResponse, ct);
            });

        // GET /api/tags — list available models
        app.MapGet("/api/tags", async (
                OllamaHandler handler) =>
            {
                var result = await handler.HandleListModelsAsync();
                return Results.Json(result);
            });

        // POST /api/show — show model information
        app.MapPost("/api/show", async (OllamaHandler handler, HttpRequest httpRequest) =>
        {
            using var reader = new StreamReader(httpRequest.Body);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<LR.Core.Models.Ollama.ShowRequest>(body);

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

        // POST /api/generate — text generation with a single prompt
        app.MapPost("/api/generate", async (
                OllamaHandler handler,
                HttpRequest httpRequest,
                HttpResponse httpResponse,
                CancellationToken ct) =>
            {
                return await handler.HandleGenerateCompletionAsync(httpRequest, httpResponse, ct);
            });

        // POST /api/embed — generate embeddings from a model
        app.MapPost("/api/embed", async (OllamaHandler handler, HttpRequest httpRequest) =>
        {
            using var reader = new StreamReader(httpRequest.Body);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<LR.Core.Models.Ollama.EmbedRequest>(body);

            if (request is null)
                return Results.BadRequest("Invalid JSON in request body");

            var result = await handler.HandleEmbeddingsAsync(request);
            if (result is Microsoft.AspNetCore.Http.IResult iResult)
                return iResult;

            return Results.Json(result!);
        });

        // GET /api/ps — list models currently loaded in memory
        app.MapGet("/api/ps", async (
                OllamaHandler handler) =>
            {
                var result = await handler.HandlePsAsync();
                return Results.Json(result);
            });

        return app;
    }
}
