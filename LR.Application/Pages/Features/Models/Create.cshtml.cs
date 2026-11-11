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

    /// <summary>What's being imported: a model file, or a checkpoint folder (TensorFold, vLLM).</summary>
    [BindProperty]
    public ImportKind Kind { get; set; } = ImportKind.File;

    public enum ImportKind { File, Checkpoint }

    /// <summary>
    /// Optional folder to scan instead of the library root — set via "?folder=" when arriving
    /// from the Models list's "+ File" link for a specific model, so candidates are scoped to
    /// that model's own folder (e.g. to pick up a sibling mmproj file or another quant someone
    /// dropped in there) instead of the whole library.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string? Folder { get; set; }

    public IReadOnlyList<string> ScannedCandidates { get; set; } = Array.Empty<string>();
    public string? ErrorMessage { get; set; }

    public ModelsCreateModel(IModelLibrary modelLibrary, IModelLibrarySettingsService settings)
    {
        _modelLibrary = modelLibrary;
        _settings = settings;
    }

    public async Task OnGetAsync()
    {
        var folder = Folder;
        if (string.IsNullOrWhiteSpace(folder))
            folder = (await _settings.GetAsync()).RootFolder;

        if (!string.IsNullOrWhiteSpace(folder))
            ScannedCandidates = await _modelLibrary.ScanFolderAsync(folder);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            ErrorMessage = Kind == ImportKind.Checkpoint ? "Enter the checkpoint's folder." : "Enter a file path.";
            await OnGetAsync();
            return Page();
        }

        // The choice decides what the path must be, so a folder isn't taken for a file or the other way round.
        var isFolder = Directory.Exists(FilePath);
        if (Kind == ImportKind.Checkpoint && !isFolder)
        {
            ErrorMessage = "A checkpoint is a folder (config.json, tokenizer and .safetensors weights). Enter the folder, or choose Model file.";
            await OnGetAsync();
            return Page();
        }
        if (Kind == ImportKind.File && isFolder)
        {
            ErrorMessage = "That's a folder. Choose Checkpoint folder to import a TensorFold / vLLM checkpoint, or enter a .gguf or .safetensors file.";
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
