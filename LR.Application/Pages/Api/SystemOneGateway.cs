using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Pages.Api;

/// <summary>
/// <c>POST /v1/systemone</c> — llama-server's TypeSafe/Jev-compatible decision API. A native
/// decision model (one with <c>{arch}.decision.type</c> in its GGUF, e.g. OpenJev, Laya, Kev)
/// scores typed <c>questions</c> (<c>choice</c>, <c>score</c>, <c>noul</c>) about a <c>state</c>
/// and returns probabilities in one forward pass; nothing is generated, and streaming isn't
/// supported. The body is already in llama-server's shape, so it's forwarded verbatim and the
/// response (<c>answers</c>/<c>usage</c>) returned as is.
/// </summary>
public class SystemOneGateway(
    ILogger<SystemOneGateway> logger,
    IServerManager serverManager,
    IPresetManager presetManager,
    IRoutingEngine routingEngine,
    IRequestQueueService queue,
    IApiRequestLogger requestLogger,
    GatewaySettings gatewaySettings,
    IApiKeyRequestContext apiKeyContext)
    : PassthroughGateway(logger, serverManager, presetManager, routingEngine, queue, requestLogger, gatewaySettings, apiKeyContext)
{
    public const string Path = "/v1/systemone";

    public async Task<IResult> HandleAsync(HttpRequest httpRequest, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(httpRequest.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);

        string? model;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Error(400, "Request body must be a JSON object.");
            if (!root.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Object)
                return Error(400, "Missing 'questions' object in request body.");
            model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        }
        catch (JsonException)
        {
            return Error(400, "Invalid JSON in request body.");
        }

        // llama-server's own endpoint takes no model — it serves the one it loaded — so a Jev
        // client pointed here may not send one. That's unambiguous only while a single decision
        // preset is reachable with this key.
        if (string.IsNullOrWhiteSpace(model))
        {
            var decisionPresets = ApiKeyContext
                .FilterAllowed(await PresetManager.GetAllPresetsAsync())
                .Where(p => !string.IsNullOrWhiteSpace(p.GgufDecisionType))
                .ToList();
            if (decisionPresets.Count != 1)
                return Error(400, decisionPresets.Count == 0
                    ? "Missing 'model' in request body, and no decision model preset is available."
                    : $"Missing 'model' in request body. Decision models: {string.Join(", ", decisionPresets.Select(p => p.Name))}.");
            model = decisionPresets[0].Name;
        }

        if (TryResolvePreset(model, out var preset) is { } rejected)
            return Error(rejected.StatusCode, rejected.Error!);

        var result = await ForwardAsync(ApiProtocol.SystemOne, Path, body, preset, Path, body,
            r => $"Decision: {r.PromptTokensProcessed} input tokens", cancellationToken);

        return result.IsSuccess
            ? Results.Content(result.Response!.Payload, "application/json")
            : Error(result.StatusCode, result.Error!);
    }

    private static IResult Error(int statusCode, string message) =>
        Results.Json(new
        {
            error = new
            {
                message,
                type = statusCode switch
                {
                    404 => "not_found_error",
                    403 => "permission_error",
                    >= 500 => "api_error",
                    _ => "invalid_request_error",
                }
            }
        }, statusCode: statusCode);
}
