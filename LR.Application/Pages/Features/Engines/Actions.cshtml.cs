using System.Text.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Application.Agents;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;

namespace LR.Application.Pages.Features.Engines;

public class EngineActionsModel : PageModel
{
    private readonly IEngineBuildManager _manager;
    private readonly EngineBuildService _buildService;
    private readonly AgentDeploymentService _agents;

    public EngineActionsModel(IEngineBuildManager manager, EngineBuildService buildService, AgentDeploymentService agents)
    {
        _manager = manager;
        _buildService = buildService;
        _agents = agents;
    }

    /// <summary>Whether the build lives on a remote agent — its jobs and files are handled there.</summary>
    private async Task<bool> IsRemoteAsync(Guid buildId) => (await _manager.GetBuildAsync(buildId))?.AgentId is not null;

    [BindProperty] public string? Command { get; set; }
    [BindProperty] public Guid BuildId { get; set; }
    [BindProperty] public Guid RecipeId { get; set; }
    [BindProperty] public bool DeleteFiles { get; set; }
    [BindProperty] public BackendType Backend { get; set; }
    [BindProperty] public string? ReleaseTag { get; set; }
    [BindProperty] public string? GitRef { get; set; }
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? RecipeJson { get; set; }
    [BindProperty] public string? Folder { get; set; }
    /// <summary>Strata: the release asset holding the engine build to install (e.g. <c>strata-windows-x64.zip</c>).</summary>
    [BindProperty] public string? Asset { get; set; }

    /// <summary>vLLM on Windows: the WSL distro to install in (the default one when empty).</summary>
    [BindProperty] public string? WslDistro { get; set; }

    /// <summary>TensorFold's Intel image: the GPU its kernels are compiled for (ocloc's name; bmg when empty).</summary>
    [BindProperty] public string? XpuDevice { get; set; }

    /// <summary>The engine a release install is for (llama.cpp when not given).</summary>
    [BindProperty] public ServerEngine? Engine { get; set; }

    public async Task<IActionResult> OnPostAsync()
    {
        return Command?.ToLowerInvariant() switch
        {
            "registerstrata" => await RunAsync(async () =>
                new { success = true, buildId = await _buildService.RegisterStrataCheckoutAsync(Folder ?? "", Name, HttpContext.RequestAborted), message = "Checkout added." }),
            "rollbackengine" => await RunAsync(async () =>
                new { success = true, buildId = await _buildService.StartStrataRollbackAsync(BuildId), message = "Rollback started." }),
            "refresh" => await RunAsync(async () =>
            {
                await _buildService.RefreshStrataAsync(BuildId, HttpContext.RequestAborted);
                return new { success = true, buildId = BuildId, message = "Refreshed." };
            }),
            "installvllm" => await RunAsync(async () =>
                new { success = true, buildId = await _buildService.StartVllmInstallAsync(Backend, ReleaseTag, WslDistro, Name), message = "Install started." }),
            "installtensorfoldxpu" => await RunAsync(async () =>
                new { success = true, buildId = await _buildService.StartTensorFoldDockerInstallAsync(GitRef, XpuDevice, Name), message = "Image build started." }),
            "installrelease" => await InstallReleaseAsync(),
            "update" => await UpdateAsync(),
            "startbuild" => await StartBuildAsync(),
            "cancel" => await CancelAsync(),
            "delete" => await DeleteAsync(),
            "checkupdate" => await CheckUpdateAsync(),
            "saverecipe" => await SaveRecipeAsync(),
            "deleterecipe" => await DeleteRecipeAsync(),
            _ => new JsonResult(new { success = false, message = $"Unknown command: {Command}" }),
        };
    }

    private static async Task<IActionResult> RunAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return new JsonResult(await action());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new JsonResult(new { success = false, message = ex.Message });
        }
    }

    private async Task<IActionResult> InstallReleaseAsync()
    {
        try
        {
            var tag = string.IsNullOrWhiteSpace(ReleaseTag) ? null : ReleaseTag.Trim();
            var name = string.IsNullOrWhiteSpace(Name) ? null : Name;
            var id = Engine == ServerEngine.Strata
                ? await _buildService.StartStrataReleaseInstallAsync(tag, Asset, name)
                : await _buildService.StartReleaseInstallAsync(Engine ?? ServerEngine.LlamaCpp, Backend, tag, name);
            return new JsonResult(new { success = true, buildId = id, message = "Install started." });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, message = ex.Message });
        }
    }

    private async Task<IActionResult> UpdateAsync()
    {
        try
        {
            var refOverride = !string.IsNullOrWhiteSpace(ReleaseTag) ? ReleaseTag!.Trim()
                : !string.IsNullOrWhiteSpace(GitRef) ? GitRef!.Trim()
                : null;
            var id = await IsRemoteAsync(BuildId)
                ? await _agents.StartEngineUpdateAsync(BuildId, refOverride)
                : await _buildService.StartUpdateAsync(BuildId, refOverride);
            return new JsonResult(new { success = true, buildId = id, message = "Update started." });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, message = ex.Message });
        }
    }

    private async Task<IActionResult> StartBuildAsync()
    {
        try
        {
            var id = await _buildService.StartSourceBuildAsync(RecipeId, string.IsNullOrWhiteSpace(GitRef) ? null : GitRef.Trim(), string.IsNullOrWhiteSpace(Name) ? null : Name);
            return new JsonResult(new { success = true, buildId = id, message = "Build started." });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, message = ex.Message });
        }
    }

    private async Task<IActionResult> CancelAsync()
    {
        var ok = await IsRemoteAsync(BuildId) ? await _agents.CancelAsync(BuildId) : _buildService.Cancel(BuildId);
        return new JsonResult(new { success = ok, message = ok ? "Cancelling…" : "That build is not running." });
    }

    private async Task<IActionResult> DeleteAsync()
    {
        try
        {
            var ok = await IsRemoteAsync(BuildId)
                ? await _agents.DeleteBuildAsync(BuildId, DeleteFiles)
                : await _manager.DeleteBuildAsync(BuildId, DeleteFiles);
            return new JsonResult(new { success = ok, message = ok ? "Build deleted." : "Build not found." });
        }
        catch (InvalidOperationException ex)
        {
            return new JsonResult(new { success = false, message = ex.Message });
        }
    }

    private async Task<IActionResult> CheckUpdateAsync()
    {
        var status = await _manager.GetUpdateStatusAsync(BuildId);
        if (status.Error is not null)
            return new JsonResult(new { success = false, message = status.Error });
        return new JsonResult(new { success = true, updateAvailable = status.UpdateAvailable, behindBy = status.BehindBy, latestTag = status.LatestTag });
    }

    private async Task<IActionResult> SaveRecipeAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(RecipeJson))
                return new JsonResult(new { success = false, message = "No recipe data." });

            var recipe = JsonSerializer.Deserialize<LlamaCppBuildRecipe>(RecipeJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Could not parse recipe.");
            var saved = await _manager.SaveRecipeAsync(recipe);
            return new JsonResult(new { success = true, recipeId = saved.Id, message = "Recipe saved." });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, message = ex.Message });
        }
    }

    private async Task<IActionResult> DeleteRecipeAsync()
    {
        var ok = await _manager.DeleteRecipeAsync(RecipeId);
        return new JsonResult(new { success = ok, message = ok ? "Recipe deleted." : "Built-in recipes can't be deleted." });
    }
}
