using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Models;
using LR.Application.Pages.Api;

namespace LR.Application.Pages.Features.Playground;

public class HistoryModel : PageModel
{
    private const int MaxRunsShown = 500;

    private readonly LRDbContext _context;

    public HistoryModel(LRDbContext context)
    {
        _context = context;
    }

    [BindProperty(SupportsGet = true)]
    public string? Preset { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Label { get; set; }

    /// <summary>Include runs that loaded the model (cold starts) in the configuration averages.</summary>
    [BindProperty(SupportsGet = true)]
    public bool IncludeCold { get; set; }

    public IReadOnlyList<string> PresetNames { get; set; } = [];
    public IReadOnlyList<string> Labels { get; set; } = [];
    public IReadOnlyList<PlaygroundRun> Runs { get; set; } = [];
    public int TotalMatching { get; set; }
    public IReadOnlyList<ConfigurationSummary> Configurations { get; set; } = [];

    /// <summary>
    /// Aggregate performance of every run made under one configuration — same preset, same
    /// launch settings (by hash) and same run label.
    /// </summary>
    public sealed record ConfigurationSummary(
        string PresetName,
        string? SettingsHash,
        string? Label,
        string? SettingsSnapshot,
        int Runs,
        int Excluded,
        double? MedianTtftMs,
        double? AvgPromptTps,
        double? AvgGenTps,
        double? AvgTotalMs,
        double? AvgOutputTokens,
        double? DraftAcceptance,
        DateTimeOffset LastRun)
    {
        public string Key => $"{PresetName} · {Label ?? "no label"} · #{SettingsHash ?? "—"}";
    }

    public async Task OnGetAsync()
    {
        PresetNames = await _context.PlaygroundRuns.Select(r => r.PresetName).Distinct().OrderBy(n => n).ToListAsync();
        Labels = await _context.PlaygroundRuns.Where(r => r.Label != null).Select(r => r.Label!).Distinct().OrderBy(l => l).ToListAsync();

        var all = await PlaygroundEndpoints.FilterRuns(_context.PlaygroundRuns.AsNoTracking(), Preset, Label)
            .OrderByDescending(r => r.Timestamp)
            .ToListAsync();

        TotalMatching = all.Count;
        Runs = all.Take(MaxRunsShown).ToList();

        Configurations = all
            .GroupBy(r => (r.PresetName, r.SettingsHash, r.Label))
            .Select(g =>
            {
                var counted = g.Where(r => r.Error is null && (IncludeCold || !r.ColdStart)).ToList();
                var drafted = counted.Where(r => r.DraftGenerated > 0).ToList();
                return new ConfigurationSummary(
                    g.Key.PresetName,
                    g.Key.SettingsHash,
                    g.Key.Label,
                    g.First().SettingsSnapshot,
                    counted.Count,
                    g.Count() - counted.Count,
                    Median(counted.Select(r => r.TimeToFirstTokenMs)),
                    Average(counted.Select(r => r.PromptTokensPerSec)),
                    Average(counted.Select(r => r.GenTokensPerSec)),
                    Average(counted.Select(r => r.TotalMs)),
                    Average(counted.Select(r => (double?)r.CompletionTokens)),
                    drafted.Count > 0 ? (double)drafted.Sum(r => r.DraftAccepted ?? 0) / drafted.Sum(r => r.DraftGenerated ?? 0) : null,
                    g.Max(r => r.Timestamp));
            })
            .OrderByDescending(c => c.LastRun)
            .ToList();
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var list = values.Where(v => v is > 0).Select(v => v!.Value).ToList();
        return list.Count > 0 ? list.Average() : null;
    }

    private static double? Median(IEnumerable<double?> values)
    {
        var list = values.Where(v => v.HasValue).Select(v => v!.Value).Order().ToList();
        if (list.Count == 0) return null;
        var mid = list.Count / 2;
        return list.Count % 2 == 1 ? list[mid] : (list[mid - 1] + list[mid]) / 2;
    }
}
