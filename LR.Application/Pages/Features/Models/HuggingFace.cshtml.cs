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
            return new JsonResult(files);
        }
        catch (Exception ex)
        {
            return new JsonResult(new { error = ex.Message }) { StatusCode = 502 };
        }
    }
}
