using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Records per-API-key, per-model token usage into hourly rollups and builds usage reports
/// (day / week / month / year) from them.
/// </summary>
public interface IApiKeyUsageService
{
    /// <summary>
    /// Adds one completed request to the current hour's usage for the key and model.
    /// </summary>
    /// <param name="apiKeyId">The authenticating key, or null when the request carried none.</param>
    /// <param name="modelName">The model (preset) name that served the request.</param>
    Task RecordAsync(Guid? apiKeyId, string modelName, Guid? presetId, long promptTokens, long generatedTokens, DateTimeOffset? at = null);

    /// <summary>
    /// Usage grouped by period, key and model, ordered by period then total tokens descending.
    /// </summary>
    Task<IReadOnlyList<ApiKeyUsageReportRow>> GetReportAsync(UsageReportQuery query);
}
