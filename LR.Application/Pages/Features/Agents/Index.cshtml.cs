using System.Text.Json;

using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using LR.Application.Agents;
using LR.Core.Agents;
using LR.Core.Data;
using LR.Core.Models;

namespace LR.Application.Pages.Features.Agents;

public class AgentsIndexModel : PageModel
{
    private readonly LRDbContext _context;
    private readonly AgentConnectionManager _connections;
    private readonly AgentSettingsService _settings;
    private readonly AgentPackageCatalog _packages;

    public AgentsIndexModel(LRDbContext context, AgentConnectionManager connections, AgentSettingsService settings, AgentPackageCatalog packages)
    {
        _context = context;
        _connections = connections;
        _settings = settings;
        _packages = packages;
    }

    public List<AgentRow> Agents { get; private set; } = new();
    public string RouterUrl { get; private set; } = string.Empty;
    public string? PublicUrl { get; private set; }
    public string EnrollmentToken { get; private set; } = string.Empty;
    public string? PackageVersion { get; private set; }
    public IReadOnlyList<(string Rid, AgentPackageEntry? Entry)> Packages { get; private set; } = [];
    public string PackageFolder => _packages.Folder;

    /// <summary>The router URL points at this machine only — agents elsewhere couldn't reach it.</summary>
    public bool RouterUrlIsLoopback =>
        Uri.TryCreate(RouterUrl, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    public string LinuxCommand => AgentInstallScripts.LinuxCommand(RouterUrl, EnrollmentToken);
    public string WindowsCommand => AgentInstallScripts.WindowsCommand(RouterUrl, EnrollmentToken);

    public async Task OnGetAsync()
    {
        var settings = await _settings.GetAsync();
        PublicUrl = settings.PublicUrl;
        RouterUrl = AgentSettingsService.ResolveRouterUrl(settings, Request);
        EnrollmentToken = settings.EnrollmentToken;
        PackageVersion = _packages.LatestVersion;
        Packages = _packages.Availability();

        var agents = await _context.RemoteAgents.AsNoTracking().OrderBy(a => a.Name).ToListAsync();
        var serverCounts = await _context.ServerInstances.Where(s => s.AgentId != null)
            .GroupBy(s => s.AgentId!.Value)
            .Select(g => new { g.Key, Total = g.Count(), Running = g.Count(s => s.Status == ServerStatus.Running) })
            .ToDictionaryAsync(x => x.Key);
        var buildCounts = await _context.EngineBuilds.Where(b => b.AgentId != null)
            .GroupBy(b => b.AgentId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        var modelCounts = await _context.LocalModels.Where(m => m.AgentId != null)
            .GroupBy(m => m.AgentId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);

        Agents = agents.Select(a =>
        {
            var live = _connections.GetConnection(a.Id);
            var info = live?.SystemInfo ?? ParseInfo(a.SystemInfoJson);
            serverCounts.TryGetValue(a.Id, out var servers);
            return new AgentRow(
                a,
                live is not null,
                live?.Hello?.Version ?? a.Version,
                info,
                servers?.Total ?? 0,
                servers?.Running ?? 0,
                buildCounts.GetValueOrDefault(a.Id),
                modelCounts.GetValueOrDefault(a.Id),
                PackageVersion is not null && (live?.Hello?.Version ?? a.Version) is { } v && !v.Equals(PackageVersion, StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }

    public static AgentSystemInfo? ParseInfo(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<AgentSystemInfo>(json, AgentProtocol.JsonOptions); }
        catch { return null; }
    }

    public static string FormatBytes(long? bytes)
    {
        if (bytes is not { } b) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = b;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}

public sealed record AgentRow(
    RemoteAgent Agent,
    bool Online,
    string? Version,
    AgentSystemInfo? Info,
    int Servers,
    int RunningServers,
    int Engines,
    int Models,
    bool Outdated);
