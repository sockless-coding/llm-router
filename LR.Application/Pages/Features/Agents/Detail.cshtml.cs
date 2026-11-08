using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using LR.Application.Agents;
using LR.Core.Agents;
using LR.Core.Data;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Application.Pages.Features.Agents;

public class AgentDetailModel : PageModel
{
    private readonly LRDbContext _context;
    private readonly AgentConnectionManager _connections;
    private readonly AgentDeploymentService _deployment;
    private readonly AgentPackageCatalog _packages;

    public AgentDetailModel(LRDbContext context, AgentConnectionManager connections, AgentDeploymentService deployment, AgentPackageCatalog packages)
    {
        _context = context;
        _connections = connections;
        _deployment = deployment;
        _packages = packages;
    }

    public RemoteAgent Agent { get; private set; } = null!;
    public AgentConnection? Live { get; private set; }
    public AgentSystemInfo? Info { get; private set; }
    public List<ServerInstance> Servers { get; private set; } = new();
    public List<EngineBuild> Builds { get; private set; } = new();
    public List<LocalModel> Models { get; private set; } = new();
    public List<LocalModel> RouterLibrary { get; private set; } = new();
    public List<LlamaCppBuildRecipe> Recipes { get; private set; } = new();
    public IReadOnlyList<AgentActivity> Activity { get; private set; } = [];
    public string? PackageVersion { get; private set; }
    public bool HasPackageForAgent { get; private set; }

    /// <summary>Backends with a prebuilt release archive for this agent's OS/arch, per engine.</summary>
    public Dictionary<ServerEngine, List<BackendType>> ReleaseBackends { get; } = new();

    public bool Online => Live is not null;
    public string EngineRoot => AgentDeploymentService.EngineRootOf(Agent);
    public string ModelRoot => AgentDeploymentService.ModelRootOf(Agent);
    public AgentJobProgress? ProgressOf(Guid id) => _deployment.GetProgress(id);

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var agent = await _context.RemoteAgents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
        if (agent is null) return NotFound();
        Agent = agent;
        Live = _connections.GetConnection(id);
        Info = Live?.SystemInfo ?? AgentsIndexModel.ParseInfo(agent.SystemInfoJson);
        Activity = _connections.GetActivity(id);
        PackageVersion = _packages.LatestVersion;
        HasPackageForAgent = _packages.Find(Live?.Hello?.Rid ?? agent.Rid) is not null;

        Servers = await _context.ServerInstances.AsNoTracking().Where(s => s.AgentId == id).OrderBy(s => s.Name).ToListAsync();
        Builds = await _context.EngineBuilds.AsNoTracking().Where(b => b.AgentId == id).OrderByDescending(b => b.CreatedAt).ToListAsync();
        Models = (await _context.LocalModels.AsNoTracking().Where(m => m.AgentId == id).ToListAsync()).OrderBy(m => m.Name).ToList();
        RouterLibrary = (await _context.LocalModels.AsNoTracking().Where(m => m.AgentId == null && m.Status == ModelStatus.Ready).ToListAsync())
            .OrderBy(m => m.Name).ToList();
        Recipes = await _context.LlamaCppBuildRecipes.AsNoTracking()
            .Where(r => r.Engine == ServerEngine.LlamaCpp || r.Engine == ServerEngine.StableDiffusionCpp)
            .OrderBy(r => r.Name).ToListAsync();

        var os = agent.IsWindows ? "win" : string.Equals(agent.Os, "macos", StringComparison.OrdinalIgnoreCase) ? "macos" : "ubuntu";
        var arch = agent.Arch?.ToLowerInvariant() is "arm64" ? "arm64" : "x64";
        foreach (var engine in new[] { ServerEngine.LlamaCpp, ServerEngine.StableDiffusionCpp })
        {
            var profile = EngineReleaseProfile.For(engine)!;
            ReleaseBackends[engine] = profile.CandidateBackends.Where(b => profile.PrimaryAssetPattern(b, os, arch) is not null).ToList();
        }
        // vLLM has no release archives to match: it installs with pip, for these backends.
        ReleaseBackends[ServerEngine.Vllm] = VllmLayout.InstallBackends(agent.IsWindows).ToList();
        return Page();
    }

    public static string EngineName(ServerEngine engine) => engine switch
    {
        ServerEngine.LlamaCpp => "llama.cpp",
        ServerEngine.StableDiffusionCpp => "stable-diffusion.cpp",
        ServerEngine.Vllm => "vLLM",
        _ => engine.ToString(),
    };
}
