using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;

namespace LR.Application.Pages.Features.Models;

public class ModelsHuggingFaceModel : PageModel
{
    private readonly IHuggingFaceClient _hfClient;

    public ModelsHuggingFaceModel(IHuggingFaceClient hfClient)
    {
        _hfClient = hfClient;
    }

    /// <summary>
    /// Set from the "?repoId=" query string when arriving from the Models list's "+ Version"
    /// link for a model already registered from this repo — lets the page skip straight to that
    /// repo's file list instead of making the user re-search for it.
    /// </summary>
    public string? PrefillRepoId { get; set; }

    /// <summary>The selected tab ("?format="): which file format to search repos for and list.</summary>
    public HfModelFormat Format { get; set; }

    public void OnGet(string? repoId, HfModelFormat format = HfModelFormat.Gguf)
    {
        PrefillRepoId = repoId;
        Format = format;
    }

    /// <summary>
    /// Link to this page for another file from <paramref name="repoId"/>, opened on the tab matching
    /// the format of <paramref name="filePath"/> (a model already downloaded from that repo).
    /// </summary>
    public static string BrowseRepoUrl(string repoId, string? filePath)
    {
        var url = $"/Features/Models/HuggingFace?repoId={Uri.EscapeDataString(repoId)}";
        return filePath?.EndsWith(HfModelFormat.Safetensors.FileExtension(), StringComparison.OrdinalIgnoreCase) == true
            ? url + $"&format={HfModelFormat.Safetensors}"
            : url;
    }

    public async Task<IActionResult> OnGetSearchAsync(string q, HfModelFormat format, bool textToImage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return new JsonResult(Array.Empty<object>());

        try
        {
            var results = await _hfClient.SearchModelsAsync(q, format, textToImage ? "text-to-image" : null, 25, ct);
            return new JsonResult(results);
        }
        catch (Exception ex)
        {
            return new JsonResult(new { error = ex.Message }) { StatusCode = 502 };
        }
    }

    public async Task<IActionResult> OnGetFilesAsync(string repoId, HfModelFormat format, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoId))
            return new JsonResult(Array.Empty<object>());

        try
        {
            // A split GGUF model is offered once, as its first shard with the size of all of them; downloading
            // it fetches every shard (ModelDownloadService). Safetensors files never match the shard pattern.
            var files = await _hfClient.ListModelFilesAsync(repoId, format, ct);
            var sizes = files.ToDictionary(f => f.Filename, f => f.SizeBytes, StringComparer.OrdinalIgnoreCase);
            return new JsonResult(files
                .Where(f => SplitGguf.Parse(f.Filename) is not { Index: > 1 })
                .Select(f =>
                {
                    var shards = SplitGguf.AllShards(f.Filename);
                    return new
                    {
                        filename = f.Filename,
                        sizeBytes = shards.All(s => sizes.GetValueOrDefault(s) is not null) ? shards.Sum(s => sizes[s]) : f.SizeBytes,
                        parts = shards.Count,
                    };
                }));
        }
        catch (Exception ex)
        {
            return new JsonResult(new { error = ex.Message }) { StatusCode = 502 };
        }
    }

    public async Task<IActionResult> OnGetDetailsAsync(string repoId, HfModelFormat format, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoId))
            return new JsonResult(new { error = "repoId is required" }) { StatusCode = 400 };

        try
        {
            var detail = await _hfClient.GetRepoDetailAsync(repoId, ct);
            if (detail is null)
                return new JsonResult(new { error = "Repo not found." }) { StatusCode = 404 };

            var modelFiles = detail.Siblings.Where(f => f.Filename.EndsWith(format.FileExtension(), StringComparison.OrdinalIgnoreCase)).ToList();
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
                fileCount = modelFiles.Count,
                totalSizeBytes = modelFiles.Sum(f => f.SizeBytes ?? 0)
            });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { error = ex.Message }) { StatusCode = 502 };
        }
    }
}
