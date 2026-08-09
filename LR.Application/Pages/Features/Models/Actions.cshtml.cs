using System.Text.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Services;

namespace LR.Application.Pages.Features.Models;

public class ModelsActionsModel : PageModel
{
    private readonly IModelLibrary _modelLibrary;
    private readonly IHuggingFaceClient _hfClient;
    private readonly ModelDownloadService _downloadService;

    public ModelsActionsModel(IModelLibrary modelLibrary, IHuggingFaceClient hfClient, ModelDownloadService downloadService)
    {
        _modelLibrary = modelLibrary;
        _hfClient = hfClient;
        _downloadService = downloadService;
    }

    [BindProperty]
    public string? Command { get; set; }

    [BindProperty]
    public Guid ModelId { get; set; }

    [BindProperty]
    public bool DeleteFile { get; set; }

    [BindProperty]
    public string? FilePath { get; set; }

    [BindProperty]
    public string? Name { get; set; }

    [BindProperty]
    public string? RepoId { get; set; }

    [BindProperty]
    public string? Filename { get; set; }

    [BindProperty]
    public string? Revision { get; set; }

    public async Task<IActionResult> OnPostAsync()
    {
        return Command?.ToLowerInvariant() switch
        {
            "import" => await HandleImportAsync(),
            "refresh" => await HandleRefreshAsync(),
            "delete" => await HandleDeleteAsync(),
            "download" => await HandleDownloadAsync(),
            "checkupdate" => await HandleCheckUpdateAsync(),
            _ => BadRequest(new { success = false, message = $"Unknown command: {Command}" })
        };
    }

    private async Task<IActionResult> HandleImportAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(FilePath))
                return JsonResult(new { success = false, message = "File path is required." });

            var model = await _modelLibrary.ImportFromPathAsync(FilePath, string.IsNullOrWhiteSpace(Name) ? null : Name);
            return JsonResult(new { success = true, modelId = model.Id, message = $"Imported '{model.Name}'." });
        }
        catch (Exception ex)
        {
            return JsonResult(new { success = false, message = ex.Message });
        }
    }

    private async Task<IActionResult> HandleRefreshAsync()
    {
        var ok = await _modelLibrary.RefreshMetadataAsync(ModelId);
        return JsonResult(new { success = ok, message = ok ? "Metadata refreshed." : "Model not found." });
    }

    private async Task<IActionResult> HandleDeleteAsync()
    {
        var ok = await _modelLibrary.DeleteAsync(ModelId, DeleteFile);
        return JsonResult(new { success = ok, message = ok ? "Model deleted." : "Model not found." });
    }

    private async Task<IActionResult> HandleDownloadAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(RepoId) || string.IsNullOrWhiteSpace(Filename))
                return JsonResult(new { success = false, message = "Repo and filename are required." });

            var revision = string.IsNullOrWhiteSpace(Revision) ? "main" : Revision;
            var modelId = await _downloadService.StartDownloadAsync(RepoId, Filename, revision, string.IsNullOrWhiteSpace(Name) ? null : Name);
            return JsonResult(new { success = true, modelId, message = "Download started." });
        }
        catch (Exception ex)
        {
            return JsonResult(new { success = false, message = ex.Message });
        }
    }

    private async Task<IActionResult> HandleCheckUpdateAsync()
    {
        var model = await _modelLibrary.GetByIdAsync(ModelId);
        if (model is null || string.IsNullOrEmpty(model.HfRepoId))
            return JsonResult(new { success = false, message = "Not a Hugging Face model." });

        var detail = await _hfClient.GetRepoDetailAsync(model.HfRepoId);
        if (detail is null)
            return JsonResult(new { success = false, message = "Could not reach Hugging Face." });

        bool updateAvailable = !string.IsNullOrEmpty(detail.Sha) && detail.Sha != model.HfRevision;
        return JsonResult(new { success = true, updateAvailable, latestRevision = detail.Sha });
    }

    private IActionResult JsonResult(object data)
    {
        return Content(JsonSerializer.Serialize(data), "application/json");
    }
}
