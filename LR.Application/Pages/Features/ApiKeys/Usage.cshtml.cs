using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Pages.Features.ApiKeys;

/// <summary>
/// Usage report UI: token usage per API key and model by day / week / month / year. The page
/// renders the controls; data comes from <c>/api/stats/usage-report</c> (which also serves CSV).
/// </summary>
public class ApiKeyUsageModel : PageModel
{
    private readonly IApiKeyManager _apiKeyManager;

    public IReadOnlyList<ApiKey> Keys { get; private set; } = new List<ApiKey>();

    /// <summary>Pre-selected key (e.g. when arriving from a key's Edit page); "none" = keyless requests.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ApiKeyId { get; set; }

    public ApiKeyUsageModel(IApiKeyManager apiKeyManager)
    {
        _apiKeyManager = apiKeyManager;
    }

    public async Task OnGetAsync()
    {
        Keys = (await _apiKeyManager.GetAllAsync()).OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
