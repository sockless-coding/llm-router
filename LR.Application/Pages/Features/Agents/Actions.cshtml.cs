using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using LR.Application.Agents;
using LR.Core.Data;
using LR.Core.Models;

namespace LR.Application.Pages.Features.Agents;

/// <summary>JSON command endpoint behind the Agents pages (same shape as the Engines/Models action pages).</summary>
public class AgentActionsModel : PageModel
{
    private readonly LRDbContext _context;
    private readonly AgentConnectionManager _connections;
    private readonly AgentDeploymentService _deployment;
    private readonly AgentSettingsService _settings;

    public AgentActionsModel(LRDbContext context, AgentConnectionManager connections, AgentDeploymentService deployment, AgentSettingsService settings)
    {
        _context = context;
        _connections = connections;
        _deployment = deployment;
        _settings = settings;
    }

    [BindProperty] public string? Command { get; set; }
    [BindProperty] public Guid AgentId { get; set; }
    [BindProperty] public Guid TargetId { get; set; }
    [BindProperty] public string? Value { get; set; }
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? HostOverride { get; set; }
    [BindProperty] public string? EngineRoot { get; set; }
    [BindProperty] public string? ModelRoot { get; set; }
    [BindProperty] public bool Flag { get; set; }
    [BindProperty] public ServerEngine Engine { get; set; } = ServerEngine.LlamaCpp;
    [BindProperty] public BackendType Backend { get; set; }
    [BindProperty] public string? ReleaseTag { get; set; }
    [BindProperty] public string? GitRef { get; set; }
    [BindProperty] public string? RepoId { get; set; }
    [BindProperty] public string? Filename { get; set; }
    [BindProperty] public string? Revision { get; set; }
    [BindProperty] public string? FilePath { get; set; }

    public async Task<IActionResult> OnPostAsync()
    {
        try
        {
            object result = Command?.ToLowerInvariant() switch
            {
                "savesettings" => await SaveSettingsAsync(),
                "setpublicurl" => await Ok(async () => await _settings.SetPublicUrlAsync(Value), "Router URL saved."),
                "regeneratetoken" => new { success = true, token = await _settings.RegenerateEnrollmentTokenAsync(), message = "New enrollment token generated. Agents already enrolled keep working." },
                "setenabled" => await SetEnabledAsync(),
                "update" => await UpdateAgentAsync(),
                "restart" => await Ok(() => _connections.SendAsync(AgentId, new LR.Core.Agents.RestartAgentMessage()), "Restart requested."),
                "delete" => await DeleteAgentAsync(),
                "reconcile" => await Ok(() => _deployment.ReconcileFilesAsync(AgentId, HttpContext.RequestAborted), "Checked the agent's files."),

                "installengine" => new { success = true, id = await _deployment.StartEngineInstallAsync(AgentId, Engine, Backend, Value, Name), message = "Install started." },
                "buildengine" => new { success = true, id = await _deployment.StartSourceBuildAsync(AgentId, TargetId, GitRef, Name), message = "Build started." },
                "updateengine" => new { success = true, id = await _deployment.StartEngineUpdateAsync(TargetId, string.IsNullOrWhiteSpace(Value) ? null : Value), message = "Update started." },
                "deleteengine" => new { success = await _deployment.DeleteBuildAsync(TargetId, Flag), message = "Build removed." },

                "downloadmodel" => await DownloadModelAsync(),
                "pullmodel" => new { success = true, id = await _deployment.StartModelPullAsync(AgentId, TargetId, Name), message = "Copy started." },
                "importmodel" => new { success = true, id = await _deployment.ImportModelAsync(AgentId, FilePath ?? "", Name), message = "Import started." },
                "refreshmodel" => await Ok(() => _deployment.RefreshModelAsync(TargetId), "Re-reading metadata on the agent."),
                "updatemodel" => new { success = true, id = await _deployment.StartModelUpdateAsync(TargetId), message = "Update started." },
                "deletemodel" => new { success = await _deployment.DeleteModelAsync(TargetId, Flag), message = "Model removed." },

                "canceljob" => new { success = await _deployment.CancelAsync(TargetId), message = "Cancelling…" },
                _ => new { success = false, message = $"Unknown command: {Command}" },
            };
            return new JsonResult(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new JsonResult(new { success = false, message = ex.Message });
        }
    }

    private static async Task<object> Ok(Func<Task> action, string message)
    {
        await action();
        return new { success = true, message };
    }

    private async Task<object> SaveSettingsAsync()
    {
        var agent = await _context.RemoteAgents.FindAsync(AgentId) ?? throw new InvalidOperationException("Agent not found.");
        if (!string.IsNullOrWhiteSpace(Name))
        {
            var name = Name.Trim();
            if (await _context.RemoteAgents.AnyAsync(a => a.Id != AgentId && a.Name == name))
                throw new InvalidOperationException($"Another agent is already called '{name}'.");
            agent.Name = name;
        }
        agent.HostOverride = string.IsNullOrWhiteSpace(HostOverride) ? null : HostOverride.Trim();
        agent.EngineRoot = string.IsNullOrWhiteSpace(EngineRoot) ? null : EngineRoot.Trim();
        agent.ModelRoot = string.IsNullOrWhiteSpace(ModelRoot) ? null : ModelRoot.Trim();
        agent.AutoUpdate = Flag;
        await _context.SaveChangesAsync();
        return new { success = true, message = "Agent settings saved." };
    }

    private async Task<object> SetEnabledAsync()
    {
        var agent = await _context.RemoteAgents.FindAsync(AgentId) ?? throw new InvalidOperationException("Agent not found.");
        if (!Flag)
        {
            var running = await _context.ServerInstances.CountAsync(s => s.AgentId == AgentId && (s.Status == ServerStatus.Running || s.Status == ServerStatus.Starting));
            if (running > 0)
                throw new InvalidOperationException($"Stop the {running} running server(s) on this agent before disabling it.");
        }
        agent.IsEnabled = Flag;
        await _context.SaveChangesAsync();
        if (!Flag)
            await _connections.DisconnectAsync(AgentId, "disabled");
        _connections.Log(AgentId, "info", Flag ? "Enabled." : "Disabled.");
        return new { success = true, message = Flag ? "Agent enabled." : "Agent disabled." };
    }

    private async Task<object> UpdateAgentAsync()
    {
        var version = await _connections.RequestUpdateAsync(AgentId, force: Flag);
        return new { success = true, message = version is null ? "The agent already runs the router's version." : $"Updating the agent to {version}…" };
    }

    private async Task<object> DeleteAgentAsync()
    {
        var agent = await _context.RemoteAgents.FindAsync(AgentId) ?? throw new InvalidOperationException("Agent not found.");
        var servers = await _context.ServerInstances.CountAsync(s => s.AgentId == AgentId);
        if (servers > 0)
            throw new InvalidOperationException($"Delete the {servers} server(s) on this agent first.");

        // The agent's engine/model rows cascade with it; presets pointing at those models just lose the link.
        _context.RemoteAgents.Remove(agent);
        await _context.SaveChangesAsync();
        await _connections.DisconnectAsync(AgentId, "deleted");
        return new { success = true, message = $"Agent '{agent.Name}' deleted. Uninstall the agent on that machine to stop it reconnecting." };
    }

    private async Task<object> DownloadModelAsync()
    {
        if (string.IsNullOrWhiteSpace(RepoId) || string.IsNullOrWhiteSpace(Filename))
            throw new InvalidOperationException("Repo and file name are required.");
        var id = await _deployment.StartModelDownloadAsync(AgentId, RepoId, Filename, Revision, Name);
        return new { success = true, id, message = "Download started." };
    }
}
