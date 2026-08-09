using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;

namespace LR.Application.Pages.Features.Models;

public class ModelsCreateModel : PageModel
{
    private readonly IModelLibrary _modelLibrary;
    private readonly IModelLibrarySettingsService _settings;

    [BindProperty]
    public string FilePath { get; set; } = string.Empty;

    [BindProperty]
    public string? Name { get; set; }

    public IReadOnlyList<string> ScannedCandidates { get; set; } = Array.Empty<string>();
    public string? ErrorMessage { get; set; }

    public ModelsCreateModel(IModelLibrary modelLibrary, IModelLibrarySettingsService settings)
    {
        _modelLibrary = modelLibrary;
        _settings = settings;
    }

    public async Task OnGetAsync()
    {
        var root = (await _settings.GetAsync()).RootFolder;
        if (!string.IsNullOrWhiteSpace(root))
            ScannedCandidates = await _modelLibrary.ScanFolderAsync(root);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            ErrorMessage = "Enter a file path.";
            await OnGetAsync();
            return Page();
        }

        try
        {
            await _modelLibrary.ImportFromPathAsync(FilePath, string.IsNullOrWhiteSpace(Name) ? null : Name);
            return RedirectToPage("Index");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            await OnGetAsync();
            return Page();
        }
    }
}
