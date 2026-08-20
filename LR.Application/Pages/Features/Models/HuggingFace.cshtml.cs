using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;

namespace LR.Application.Pages.Features.Models;

public class ModelsHuggingFaceModel : PageModel
{
    private readonly IHuggingFaceClient _hfClient;

    public ModelsHuggingFaceModel(IHuggingFaceClient hfClient)
    {
        _hfClient = hfClient;
    }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnGetSearchAsync(string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return new JsonResult(Array.Empty<object>());

        try
        {
            var results = await _hfClient.SearchModelsAsync(q, 25, ct);
            return new JsonResult(results);
        }
        catch (Exception ex)
        {
            return new JsonResult(new { error = ex.Message }) { StatusCode = 502 };
        }
    }

    public async Task<IActionResult> OnGetFilesAsync(string repoId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoId))
            return new JsonResult(Array.Empty<object>());

        try
        {
            var files = await _hfClient.ListGgufFilesAsync(repoId, ct);
            return new JsonResult(files.Select(f => new { filename = f.Filename, sizeBytes = f.SizeBytes }));
        }
        catch (Exception ex)
        {
            return new JsonResult(new { error = ex.Message }) { StatusCode = 502 };
        }
    }

    public async Task<IActionResult> OnGetDetailsAsync(string repoId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoId))
            return new JsonResult(new { error = "repoId is required" }) { StatusCode = 400 };

        try
        {
            var detail = await _hfClient.GetRepoDetailAsync(repoId, ct);
            if (detail is null)
                return new JsonResult(new { error = "Repo not found." }) { StatusCode = 404 };

            var ggufFiles = detail.Siblings.Where(f => f.Filename.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)).ToList();
            return new JsonResult(new
            {
                id = detail.Id,
                author = detail.Author,
                downloads = detail.Downloads,
                likes = detail.Likes,
                lastModified = detail.LastModified,
                libraryName = detail.LibraryName,
                pipelineTag = detail.PipelineTag,
                license = detail.CardData?.License,
                tags = detail.Tags,
                fileCount = ggufFiles.Count,
                totalSizeBytes = ggufFiles.Sum(f => f.SizeBytes ?? 0)
            });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { error = ex.Message }) { StatusCode = 502 };
        }
    }
}
