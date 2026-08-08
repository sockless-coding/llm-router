using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Pages.Features.Settings;

public class IndexModel : PageModel
{
    public string StatusMessage { get; set; } = string.Empty;
    public bool CleanupRan { get; set; }
    public long DeletedCount { get; set; }

    private readonly IOptionsSnapshot<GatewaySettings> _settings;
    private readonly IApiRequestLogger _requestLogger;

    // Bound form fields
    [BindProperty]
    public bool EnableRequestLogging { get; set; }

    [BindProperty]
    public bool LogFullPayloads { get; set; }

    [BindProperty]
    public int RequestLogRetentionDays { get; set; } = 7;

    [BindProperty]
    public int MaxQueueSize { get; set; } = 100;

    [BindProperty]
    public int QueueTimeoutSeconds { get; set; } = 300;

    // Path to appsettings.json for persisting changes
    private static string ConfigPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

    public IndexModel(
        IOptionsSnapshot<GatewaySettings> settings,
        IApiRequestLogger requestLogger)
    {
        _settings = settings;
        _requestLogger = requestLogger;
    }

    public void OnGet()
    {
        var s = _settings.Value;
        EnableRequestLogging = s.EnableRequestLogging;
        LogFullPayloads = s.LogFullPayloads;
        RequestLogRetentionDays = s.RequestLogRetentionDays;
        MaxQueueSize = s.MaxQueueSize;
        QueueTimeoutSeconds = s.QueueTimeoutSeconds;
    }

    public async Task<IActionResult> OnPostAsync([FromForm] string? action)
    {
        if (action == "cleanup")
        {
            try
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, RequestLogRetentionDays));
                DeletedCount = await _requestLogger.DeleteOlderThanAsync(cutoff);
                CleanupRan = true;
                StatusMessage = $"Deleted {DeletedCount} old log entries.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Cleanup failed: {ex.Message}";
            }
        }
        else if (action == "save")
        {
            try
            {
                // Read current config, update the Gateway section, write back
                var json = System.IO.File.ReadAllText(ConfigPath);
                using var doc = JsonDocument.Parse(json);
                using var stream = new MemoryStream();
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

                // Deep clone with modifications — for simplicity we re-serialize the whole file
                var raw = System.Text.Json.JsonSerializer.Serialize(doc.RootElement);
                var config = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(raw);

                if (config != null && config.TryGetValue("Gateway", out var gatewayObj))
                {
                    // Update settings in memory — the IOptionsSnapshot will pick up on next request from file
                    var gwRaw = System.Text.Json.JsonSerializer.Serialize(gatewayObj);
                    var gatewayDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(gwRaw);

                    if (gatewayDict != null)
                    {
                        gatewayDict["EnableRequestLogging"] = EnableRequestLogging;
                        gatewayDict["LogFullPayloads"] = LogFullPayloads;
                        gatewayDict["RequestLogRetentionDays"] = RequestLogRetentionDays;
                        gatewayDict["MaxQueueSize"] = MaxQueueSize;
                        gatewayDict["QueueTimeoutSeconds"] = QueueTimeoutSeconds;
                        config["Gateway"] = gatewayDict;
                    }
                }

                var updatedJson = System.Text.Json.JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(ConfigPath, updatedJson);
                StatusMessage = "Settings saved successfully.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to save settings: {ex.Message}";
            }
        }

        // Re-read values for display
        var s = _settings.Value;
        EnableRequestLogging = s.EnableRequestLogging;
        LogFullPayloads = s.LogFullPayloads;
        RequestLogRetentionDays = s.RequestLogRetentionDays;
        MaxQueueSize = s.MaxQueueSize;
        QueueTimeoutSeconds = s.QueueTimeoutSeconds;

        return Page();
    }
}
