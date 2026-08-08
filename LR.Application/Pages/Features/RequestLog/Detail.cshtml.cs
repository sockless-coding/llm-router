using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Pages.Features.RequestLog;

public class DetailModel : PageModel
{
    public ApiRequestLog? Log { get; set; }

    private readonly IApiRequestLogger _requestLogger;

    public DetailModel(IApiRequestLogger requestLogger)
    {
        _requestLogger = requestLogger;
    }

    public async Task<IActionResult> OnGetAsync([FromQuery] Guid id)
    {
        Log = await _requestLogger.GetByIdAsync(id);
        if (Log is null) return NotFound();
        return Page();
    }
}
