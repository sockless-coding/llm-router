using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Application.Services;

namespace LR.Application.Pages.Features.Settings;

/// <summary>
/// JSON endpoint behind the Settings page's Updates card: GET returns the current
/// <see cref="AppUpdateStatus"/> (polled while an install runs and while the router restarts);
/// POST runs <c>check</c> or <c>install</c>.
/// </summary>
public class UpdateModel : PageModel
{
    private readonly AppUpdateService _updates;

    public UpdateModel(AppUpdateService updates)
    {
        _updates = updates;
    }

    [BindProperty] public string? Command { get; set; }
    [BindProperty] public bool Force { get; set; }

    public IActionResult OnGet() => new JsonResult(_updates.GetStatus());

    public async Task<IActionResult> OnPostAsync()
    {
        switch (Command?.ToLowerInvariant())
        {
            case "check":
                return new JsonResult(new { success = true, status = await _updates.CheckAsync(Force, HttpContext.RequestAborted) });
            case "install":
                try
                {
                    _updates.StartInstall();
                    return new JsonResult(new { success = true, status = _updates.GetStatus() });
                }
                catch (InvalidOperationException ex)
                {
                    return new JsonResult(new { success = false, message = ex.Message, status = _updates.GetStatus() });
                }
            default:
                return new JsonResult(new { success = false, message = $"Unknown command: {Command}" });
        }
    }
}
