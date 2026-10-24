using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;
using LR.Core.Services.EngineBuilds;

namespace LR.Application.Pages.Api;

/// <summary>
/// Admin-side endpoints behind the Playground's Generate page (image and video presets). Unlike chat,
/// these don't reuse a public API handler: OpenAI's images endpoint has no progress, no image inputs
/// and no video, so generation runs on the engine's native job API (<see cref="IGenerationJobProvider"/>).
/// It still goes through routing — model auto-start, fallback, memory groups, the concurrency slot —
/// and the slot is held until the job ends, so idle-unload and memory-group eviction leave the model
/// alone mid-job. Mapped under <c>/api/playground</c> by <see cref="PlaygroundEndpoints"/>.
/// </summary>
public static class PlaygroundGenerationEndpoints
{
    private static readonly TimeSpan JobPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RouteRetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapPlaygroundGenerationEndpoints(this RouteGroupBuilder group)
    {
        // Image/video presets, plus whether each one's model is loaded and the preset's own
        // generation defaults — shown as placeholders until the loaded server's capabilities say more.
        group.MapGet("/generation/presets", (IPresetManager presetManager, IServerManager serverManager, IEngineCatalog engines) =>
        {
            var servers = serverManager.GetAllInstances().ToDictionary(s => s.Id);
            var presets = presetManager.GetAllPresets()
                .OfModality(servers.Values, engines, EngineModality.Image)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p =>
                {
                    servers.TryGetValue(p.ServerInstanceId, out var server);
                    string? setting(string key) => p.EngineSettings.GetValueOrDefault(key) is { Length: > 0 } v ? v : null;
                    return new
                    {
                        id = p.Id,
                        name = p.Name,
                        serverName = server?.Name,
                        serverStatus = server?.Status.ToString(),
                        loaded = server is { Status: ServerStatus.Running } && server.ActivePresetId == p.Id,
                        modelFile = string.IsNullOrEmpty(p.ModelPath) ? null : Path.GetFileName(p.ModelPath),
                        defaults = new Dictionary<string, string?>
                        {
                            ["width"] = setting(StableDiffusionPresetSettings.Width),
                            ["height"] = setting(StableDiffusionPresetSettings.Height),
                            ["sample_steps"] = setting(StableDiffusionPresetSettings.Steps),
                            ["txt_cfg"] = setting(StableDiffusionPresetSettings.CfgScale),
                            ["distilled_guidance"] = setting(StableDiffusionPresetSettings.Guidance),
                            ["sample_method"] = setting(StableDiffusionPresetSettings.SamplingMethod),
                            ["scheduler"] = setting(StableDiffusionPresetSettings.Scheduler),
                            ["seed"] = setting(StableDiffusionPresetSettings.Seed),
                            ["negative_prompt"] = setting(StableDiffusionPresetSettings.NegativePrompt),
                        },
                    };
                });
            return Results.Json(presets);
        });

        // The loaded model's capability document (image or video mode, defaults, samplers, LoRAs,
        // preview modes, limits) — only while the preset is what its server is running.
        group.MapGet("/generation/capabilities", async (Guid presetId, IServerManager serverManager, CancellationToken ct) =>
        {
            var server = serverManager.GetAllInstances().FirstOrDefault(s => s.ActivePresetId == presetId && s.Status == ServerStatus.Running);
            if (server is null || serverManager.GetProvider(server.Id) is not IGenerationJobProvider provider)
                return Results.Json(new { loaded = false });

            var capabilities = await provider.GetCapabilitiesAsync(ct);
            return capabilities is null
                ? Results.Json(new { loaded = true, error = "The server didn't report its capabilities." })
                : Results.Content(capabilities, "application/json");
        });

        group.MapPost("/generation/run", RunAsync);

        return group;
    }

    /// <summary>
    /// Runs one generation and streams it back as server-sent events: <c>status</c> (phase —
    /// loading, waiting, queued, generating — plus step progress), <c>preview</c> (the latest preview
    /// frame), then one of <c>result</c> (the job's result plus timings), <c>error</c> or
    /// <c>cancelled</c>. Closing the request cancels the job on the server.
    /// </summary>
    private static async Task RunAsync(
        GenerationInput input,
        HttpContext http,
        IPresetManager presetManager,
        IServerManager serverManager,
        IServiceScopeFactory scopeFactory,
        IEngineCatalog engines,
        IApiRequestLogger requestLogger,
        ILoggerFactory loggerFactory)
    {
        var ct = http.RequestAborted;
        var preset = presetManager.GetById(input.PresetId);
        var owner = preset is null ? null : serverManager.GetAllInstances().FirstOrDefault(s => s.Id == preset.ServerInstanceId);
        if (preset is null || owner is null || engines.ModalityOf(owner.Engine) != EngineModality.Image)
        {
            http.Response.StatusCode = preset is null ? 404 : 400;
            await http.Response.WriteAsJsonAsync(new { error = preset is null ? "Preset not found." : "Not an image or video preset." }, ct);
            return;
        }

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        async Task Send(string evt, object data)
        {
            await http.Response.WriteAsync($"event: {evt}\ndata: {JsonSerializer.Serialize(data, EventJson)}\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }

        var logger = loggerFactory.CreateLogger(typeof(PlaygroundGenerationEndpoints));
        var kind = string.Equals(input.Kind, "video", StringComparison.OrdinalIgnoreCase) ? GenerationJobKind.Video : GenerationJobKind.Image;
        var payload = (input.Request ?? new JsonObject()).ToJsonString();
        var sw = Stopwatch.StartNew();
        bool coldStart = !(owner.Status == ServerStatus.Running && owner.ActivePresetId == preset.Id);
        Guid logId = Guid.Empty;

        try
        {
            using var decision = await AcquireServerAsync(preset, scopeFactory, serverManager, Send, ct);
            var server = decision.Server;
            var loadMs = sw.Elapsed.TotalMilliseconds;
            if (serverManager.GetProvider(server.Id) is not IGenerationJobProvider provider)
            {
                await Send("error", new { message = $"{server.Name}'s engine has no generation job API." });
                return;
            }

            var endpoint = kind == GenerationJobKind.Video ? "/sdcpp/v1/vid_gen" : "/sdcpp/v1/img_gen";
            try { logId = await requestLogger.LogIncomingAsync(ApiProtocol.OpenAI, endpoint, LargeValueRedactor.Redact(payload), preset.Name); }
            catch { /* Logging failure shouldn't block the request */ }

            await Send("status", new { phase = "submitting", server = server.Name });
            var jobId = await provider.SubmitJobAsync(kind, payload, ct);
            try
            {
                await FollowJobAsync(provider, jobId, Send, async (job, status) =>
                {
                    var totalMs = sw.Elapsed.TotalMilliseconds;
                    var generationMs = Long(job["started"]) is long started && Long(job["completed"]) is long completed
                        ? completed - started
                        : (double?)null;
                    switch (status)
                    {
                        case "completed":
                            await Send("result", new
                            {
                                kind = kind == GenerationJobKind.Video ? "video" : "image",
                                result = job["result"],
                                server = server.Name,
                                coldStart,
                                loadMs,
                                generationMs,
                                totalMs,
                            });
                            await LogOutcomeAsync(requestLogger, logId, server, preset, job, totalMs, null);
                            break;
                        case "failed":
                            var message = Text(job["error"]?["message"]) ?? "Generation failed.";
                            await Send("error", new { message });
                            await LogOutcomeAsync(requestLogger, logId, server, preset, job, totalMs, message);
                            break;
                        default:
                            await Send("cancelled", new { });
                            await LogOutcomeAsync(requestLogger, logId, server, preset, job, totalMs, "Cancelled.");
                            break;
                    }
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The page stopped (or went away) — stop the job too, or it keeps the server busy.
                using var cancelCts = new CancellationTokenSource(CancelTimeout);
                try { await provider.CancelJobAsync(jobId, cancelCts.Token); }
                catch (Exception ex) { logger.LogDebug(ex, "Cancelling generation job {JobId} failed", jobId); }
                if (logId != Guid.Empty) { try { await requestLogger.LogErrorAsync(logId, "Cancelled by the Playground.", 499); } catch { } }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Closed while waiting for the server — nothing was submitted.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Playground generation failed for preset {Preset}", preset.Name);
            var message = ex is HttpRequestException or InvalidOperationException ? ex.Message : BackendErrorClassifier.ToClientMessage(ex);
            if (logId != Guid.Empty) { try { await requestLogger.LogErrorAsync(logId, message, 502); } catch { } }
            try { await Send("error", new { message }); } catch { /* client gone */ }
        }
    }

    /// <summary>
    /// Routes until a server running <paramref name="preset"/> (or its fallback) has a free slot,
    /// reporting whether the wait is a model load or a busy server. Routing (re)starts the model
    /// itself; a start that fails ends the wait with the server's error. Each attempt routes in a
    /// fresh scope: the routing engine's DbContext would otherwise keep serving the server row it
    /// read on the first attempt, still "Starting", and never see the model come up.
    /// </summary>
    private static async Task<RouteDecision> AcquireServerAsync(
        ModelPreset preset, IServiceScopeFactory scopeFactory, IServerManager serverManager,
        Func<string, object, Task> send, CancellationToken ct)
    {
        var routeRequest = new RouteRequest { ModelName = preset.Name, PresetId = preset.Id, Modality = EngineModality.Image };
        string? lastPhase = null;
        for (int attempt = 0; ; attempt++)
        {
            using (var scope = scopeFactory.CreateScope())
            {
                var routingEngine = scope.ServiceProvider.GetRequiredService<IRoutingEngine>();
                if (await routingEngine.RouteAsync(routeRequest, ct) is { } decision)
                    return decision;
            }

            var server = serverManager.GetAllInstances().FirstOrDefault(s => s.Id == preset.ServerInstanceId);
            // The first attempt may have just kicked off a restart of an errored server; after that,
            // Error means the start failed.
            if (server is { Status: ServerStatus.Error } && attempt > 0)
                throw new InvalidOperationException(server.LastErrorMessage ?? $"{server.Name} failed to start — check its logs on the Servers page.");

            var phase = server is { Status: ServerStatus.Running } && server.ActivePresetId == preset.Id ? "waiting" : "loading";
            if (phase != lastPhase)
            {
                await send("status", new { phase, server = server?.Name });
                lastPhase = phase;
            }
            await Task.Delay(RouteRetryInterval, ct);
        }
    }

    /// <summary>
    /// Polls the job until it ends, forwarding queue/step changes and each new preview frame, then
    /// hands the final job document to <paramref name="onFinished"/>.
    /// </summary>
    private static async Task FollowJobAsync(
        IGenerationJobProvider provider, string jobId, Func<string, object, Task> send,
        Func<JsonObject, string, Task> onFinished, CancellationToken ct)
    {
        string? lastStatus = null;
        string? lastPreview = null;
        while (true)
        {
            await Task.Delay(JobPollInterval, ct);
            if (JsonNode.Parse(await provider.GetJobAsync(jobId, ct)) is not JsonObject job)
                continue;

            var status = Text(job["status"]) ?? "";
            if (status is "completed" or "failed" or "cancelled")
            {
                await onFinished(job, status);
                return;
            }

            var preview = job["preview"] as JsonObject;
            var progress = new
            {
                phase = status == "queued" ? "queued" : "generating",
                queuePosition = (int?)Long(job["queue_position"]),
                pass = (int?)Long(preview?["pass"]),
                step = (int?)Long(preview?["step"]),
                totalSteps = (int?)Long(preview?["total_steps"]),
            };
            var statusKey = JsonSerializer.Serialize(progress);
            if (statusKey != lastStatus)
            {
                await send("status", progress);
                lastStatus = statusKey;
            }

            if (Text(preview?["b64_json"]) is { Length: > 0 } frame && frame != lastPreview)
            {
                await send("preview", new { b64 = frame });
                lastPreview = frame;
            }
        }
    }

    private static async Task LogOutcomeAsync(
        IApiRequestLogger requestLogger, Guid logId, ServerInstance server, ModelPreset preset,
        JsonObject job, double totalMs, string? error)
    {
        if (logId == Guid.Empty)
            return;
        try
        {
            job.Remove("preview");
            await requestLogger.LogBackendResponseAsync(logId, LargeValueRedactor.Redact(job.ToJsonString()));
            if (error is not null)
                await requestLogger.LogErrorAsync(logId, error, 502);
            else
                await requestLogger.LogCompletionAsync(logId, server, preset, new RouteResponse { TotalLatencyMs = totalMs }, 200,
                    $"Playground generation finished in {totalMs / 1000:F1}s", false, false);
        }
        catch { /* Logging failure shouldn't affect the result */ }
    }

    private static long? Long(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <param name="Kind"><c>image</c> or <c>video</c>.</param>
    /// <param name="Request">The engine's native request body (prompt, size, sampling, images…).</param>
    public sealed record GenerationInput(Guid PresetId, string? Kind, JsonObject? Request);
}
