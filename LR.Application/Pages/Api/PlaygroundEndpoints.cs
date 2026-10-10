using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;
using LR.Application.Pages.Features.Presets;

namespace LR.Application.Pages.Api;

/// <summary>
/// Admin-side endpoints backing the Playground page. These live under /api/playground (not /v1),
/// so they're served on the admin port even when <see cref="GatewaySettings.RoutingPort"/> splits
/// the routing API off onto its own listener, and they aren't behind <see cref="ApiKeyAuthFilter"/> —
/// the dashboard itself is the trust boundary, same as every other admin page.
/// </summary>
public static class PlaygroundEndpoints
{
    public static IEndpointRouteBuilder MapPlaygroundEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/playground");

        // Presets the Playground can chat with, plus whether each one's model is loaded right now —
        // the page re-reads this before every send to flag runs that include a model (re)load.
        group.MapGet("/presets", (IPresetManager presetManager, IServerManager serverManager, IChatTemplateVariableExtractor templateVariableExtractor) =>
        {
            var servers = serverManager.GetAllInstances().ToDictionary(s => s.Id);
            var presets = presetManager.GetAllPresets()
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p =>
                {
                    servers.TryGetValue(p.ServerInstanceId, out var server);
                    return new
                    {
                        id = p.Id,
                        name = p.Name,
                        serverName = server?.Name,
                        serverStatus = server?.Status.ToString(),
                        loaded = server is { Status: ServerStatus.Running } && server.ActivePresetId == p.Id,
                        contextSize = p.ContextSize ?? p.GgufContextLength,
                        parameterSize = p.GgufParameterSize,
                        quantization = p.GgufQuantizationLevel,
                        // The preset's own sampling values, keyed by request parameter name — shown
                        // as placeholders so it's clear what a blank override field falls back to.
                        defaults = new Dictionary<string, object?>
                        {
                            ["temperature"] = p.Temperature,
                            ["top_p"] = p.TopP,
                            ["top_k"] = p.TopK,
                            ["min_p"] = p.MinP,
                            ["max_tokens"] = p.PredictN,
                            ["seed"] = p.Seed,
                            ["reasoning_effort"] = p.ReasoningEffort,
                        },
                        reasoningEffortOptions = ReasoningCapabilityDetector.SupportsReasoningEffort(p.GgufChatTemplate, templateVariableExtractor)
                            ? ReasoningCapabilityDetector.GetReasoningEffortOptions(p.GgufChatTemplate, templateVariableExtractor)
                            : null,
                    };
                });
            return Results.Json(presets);
        });

        // Chat goes through the exact same pipeline as /v1/chat/completions (routing, model
        // auto-start, stats, request log), so what's measured here is what API clients get.
        group.MapPost("/chat", (OpenAiHandler handler, HttpRequest httpRequest, HttpResponse httpResponse, CancellationToken ct) =>
            handler.HandleChatCompletionAsync(httpRequest, httpResponse, ct));

        group.MapPost("/runs", async (PlaygroundRunInput input, LRDbContext db) =>
        {
            var preset = await db.ModelPresets.AsNoTracking()
                .Include(p => p.ServerInstance)
                .FirstOrDefaultAsync(p => p.Id == input.PresetId);
            if (preset is null)
                return Results.NotFound(new { error = "Preset not found." });

            var run = new PlaygroundRun
            {
                PresetId = preset.Id,
                PresetName = preset.Name,
                ServerName = preset.ServerInstance?.Name,
                Engine = preset.ServerInstance?.Engine,
                ModelFile = string.IsNullOrEmpty(preset.ModelPath) ? null : Path.GetFileName(preset.ModelPath),
                RequestParams = input.RequestParams,
                Label = string.IsNullOrWhiteSpace(input.Label) ? null : input.Label.Trim(),
                SessionId = input.SessionId,
                ColdStart = input.ColdStart,
                MessageCount = input.MessageCount,
                Prompt = input.Prompt,
                Response = input.Response,
                Reasoning = string.IsNullOrEmpty(input.Reasoning) ? null : input.Reasoning,
                FinishReason = input.FinishReason,
                Error = input.Error,
                TimeToFirstTokenMs = input.TimeToFirstTokenMs,
                TotalMs = input.TotalMs,
                PromptTokens = input.PromptTokens,
                CachedTokens = input.CachedTokens,
                CompletionTokens = input.CompletionTokens,
                PromptMs = input.PromptMs,
                PromptTokensPerSec = input.PromptTokensPerSec,
                GenerationMs = input.GenerationMs,
                GenTokensPerSec = input.GenTokensPerSec,
                DraftAccepted = input.DraftAccepted,
                DraftGenerated = input.DraftGenerated,
            };

            if (!string.IsNullOrEmpty(preset.ModelPath))
            {
                run.SettingsSnapshot = PresetPreview.Build(preset, preset.ServerInstance?.Engine ?? ServerEngine.LlamaCpp).CommandLine;
                run.SettingsHash = HashSettings(run.SettingsSnapshot);
            }

            db.PlaygroundRuns.Add(run);
            await db.SaveChangesAsync();

            return Results.Json(new { id = run.Id, settingsHash = run.SettingsHash });
        });

        group.MapPost("/runs/delete", async (DeleteRunsInput input, LRDbContext db) =>
        {
            var deleted = await db.PlaygroundRuns.Where(r => input.Ids.Contains(r.Id)).ExecuteDeleteAsync();
            return Results.Json(new { deleted });
        });

        group.MapGet("/runs/export", async (string? preset, string? label, LRDbContext db) =>
        {
            var runs = await FilterRuns(db.PlaygroundRuns.AsNoTracking(), preset, label)
                .OrderBy(r => r.Timestamp)
                .ToListAsync();

            return Results.File(Encoding.UTF8.GetBytes(BuildCsv(runs)), "text/csv",
                $"playground-runs-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
        });

        return app;
    }

    /// <summary>
    /// Shared filter for the history page and the CSV export, so an export always contains
    /// exactly the rows the page was showing. Filters on the preset *name* recorded with the run
    /// (not its id), so runs for a since-deleted preset stay filterable.
    /// </summary>
    public static IQueryable<PlaygroundRun> FilterRuns(IQueryable<PlaygroundRun> query, string? preset, string? label)
    {
        if (!string.IsNullOrWhiteSpace(preset))
            query = query.Where(r => r.PresetName == preset);
        if (!string.IsNullOrWhiteSpace(label))
            query = query.Where(r => r.Label == label);
        return query;
    }

    private static string HashSettings(string snapshot)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(snapshot));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    private static string BuildCsv(IEnumerable<PlaygroundRun> runs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("timestamp,preset,server,engine,model_file,settings_hash,label,session_id,cold_start,message_count," +
                      "ttft_ms,total_ms,prompt_tokens,cached_tokens,completion_tokens,prompt_ms,prompt_tps,generation_ms,gen_tps," +
                      "draft_accepted,draft_generated,finish_reason,error,request_params,settings,prompt");

        foreach (var r in runs)
        {
            string[] fields =
            [
                r.Timestamp.UtcDateTime.ToString("O"), r.PresetName, r.ServerName ?? "", r.Engine?.ToString() ?? "",
                r.ModelFile ?? "", r.SettingsHash ?? "", r.Label ?? "", r.SessionId.ToString(), r.ColdStart ? "true" : "false",
                Num(r.MessageCount), Num(r.TimeToFirstTokenMs), Num(r.TotalMs), Num(r.PromptTokens), Num(r.CachedTokens),
                Num(r.CompletionTokens), Num(r.PromptMs), Num(r.PromptTokensPerSec), Num(r.GenerationMs), Num(r.GenTokensPerSec),
                Num(r.DraftAccepted), Num(r.DraftGenerated), r.FinishReason ?? "", r.Error ?? "", r.RequestParams ?? "",
                r.SettingsSnapshot ?? "", r.Prompt ?? "",
            ];
            sb.AppendLine(string.Join(',', fields.Select(Csv)));
        }

        return sb.ToString();

        static string Num(IFormattable? value) =>
            value?.ToString(null, CultureInfo.InvariantCulture) ?? "";

        static string Csv(string value) =>
            value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    public sealed record DeleteRunsInput(List<Guid> Ids);

    public sealed record PlaygroundRunInput
    {
        public Guid PresetId { get; init; }
        public Guid SessionId { get; init; }
        public string? Label { get; init; }
        public bool ColdStart { get; init; }
        public int MessageCount { get; init; }
        public string? RequestParams { get; init; }
        public string? Prompt { get; init; }
        public string? Response { get; init; }
        public string? Reasoning { get; init; }
        public string? FinishReason { get; init; }
        public string? Error { get; init; }
        public double? TimeToFirstTokenMs { get; init; }
        public double? TotalMs { get; init; }
        public int PromptTokens { get; init; }
        public int? CachedTokens { get; init; }
        public int CompletionTokens { get; init; }
        public double? PromptMs { get; init; }
        public double? PromptTokensPerSec { get; init; }
        public double? GenerationMs { get; init; }
        public double? GenTokensPerSec { get; init; }
        public int? DraftAccepted { get; init; }
        public int? DraftGenerated { get; init; }
    }
}
