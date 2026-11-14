using System.Globalization;
using System.Text;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Pages.Api.Stats;

/// <summary>
/// <c>GET /api/stats/usage-report</c>: token usage per API key and model, bucketed by day, week,
/// month, year or the whole range.
/// <list type="bullet">
/// <item><c>period</c> — <c>day</c> (default), <c>week</c> (ISO, Monday start), <c>month</c>, <c>year</c>, <c>total</c>.</item>
/// <item><c>from</c>, <c>to</c> — inclusive local dates (<c>yyyy-MM-dd</c>); default the last 30 days.</item>
/// <item><c>tz</c> — IANA or Windows timezone id the dates and periods are in; default the server's.</item>
/// <item><c>apiKeyId</c> — a key id, or <c>none</c> for keyless requests; default all keys.</item>
/// <item><c>model</c> — a model (preset) name; default all models.</item>
/// <item><c>format</c> — <c>json</c> (default) or <c>csv</c> (downloads as a file).</item>
/// </list>
/// </summary>
public class UsageReportModel : PageModel
{
    private readonly IApiKeyUsageService _usage;

    public UsageReportModel(IApiKeyUsageService usage)
    {
        _usage = usage;
    }

    [BindProperty(SupportsGet = true)] public string? Period { get; set; }
    [BindProperty(SupportsGet = true)] public string? From { get; set; }
    [BindProperty(SupportsGet = true)] public string? To { get; set; }
    [BindProperty(SupportsGet = true)] public string? Tz { get; set; }
    [BindProperty(SupportsGet = true)] public string? ApiKeyId { get; set; }
    [BindProperty(SupportsGet = true, Name = "model")] public string? ModelName { get; set; }
    [BindProperty(SupportsGet = true)] public string? Format { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        UsagePeriod period = UsagePeriod.Day;
        if (!string.IsNullOrEmpty(Period) && !Enum.TryParse(Period, ignoreCase: true, out period))
            return BadRequest(new { error = "period must be one of: day, week, month, year, total." });

        var tz = ResolveTimeZone(Tz);
        if (tz is null)
            return BadRequest(new { error = $"Unknown timezone '{Tz}'." });

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).DateTime);
        if (!TryParseDate(To, today, out var to) || !TryParseDate(From, to.AddDays(-29), out var from))
            return BadRequest(new { error = "from/to must be dates in yyyy-MM-dd format." });
        if (from > to) (from, to) = (to, from);

        Guid? keyId = null;
        if (!string.IsNullOrEmpty(ApiKeyId))
        {
            if (string.Equals(ApiKeyId, "none", StringComparison.OrdinalIgnoreCase)) keyId = Guid.Empty;
            else if (Guid.TryParse(ApiKeyId, out var parsed)) keyId = parsed;
            else return BadRequest(new { error = "apiKeyId must be a key id or 'none'." });
        }

        var rows = await _usage.GetReportAsync(new UsageReportQuery(
            period, from, to, tz, keyId, string.IsNullOrWhiteSpace(ModelName) ? null : ModelName));

        if (string.Equals(Format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            var fileName = $"llm-router-usage-{period.ToString().ToLowerInvariant()}-{from:yyyy-MM-dd}-to-{to:yyyy-MM-dd}.csv";
            return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ToCsv(rows, period))).ToArray(),
                "text/csv; charset=utf-8", fileName);
        }

        return new JsonResult(new
        {
            period = period.ToString().ToLowerInvariant(),
            from = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            to = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            timeZone = tz.Id,
            rows = rows.Select(r => new
            {
                periodStart = r.PeriodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                apiKeyId = r.ApiKeyId == Guid.Empty ? null : (Guid?)r.ApiKeyId,
                apiKeyName = r.ApiKeyName,
                apiKeyPrefix = r.ApiKeyPrefix,
                keyDeleted = r.KeyDeleted,
                model = r.ModelName,
                requestCount = r.RequestCount,
                inputTokens = r.PromptTokens,
                outputTokens = r.GeneratedTokens,
                totalTokens = r.TotalTokens,
            }),
        });
    }

    private static TimeZoneInfo? ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Local;
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz) ? tz : null;
    }

    private static bool TryParseDate(string? value, DateOnly fallback, out DateOnly date)
    {
        if (string.IsNullOrWhiteSpace(value)) { date = fallback; return true; }
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    internal static string ToCsv(IEnumerable<ApiKeyUsageReportRow> rows, UsagePeriod period)
    {
        var sb = new StringBuilder();
        sb.AppendLine("period,period_start,api_key_id,api_key_name,api_key_prefix,key_deleted,model,requests,input_tokens,output_tokens,total_tokens");
        var p = period.ToString().ToLowerInvariant();
        foreach (var r in rows)
        {
            sb.Append(p).Append(',')
              .Append(r.PeriodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.ApiKeyId == Guid.Empty ? "" : r.ApiKeyId.ToString()).Append(',')
              .Append(Csv(r.ApiKeyName)).Append(',')
              .Append(Csv(r.ApiKeyPrefix)).Append(',')
              .Append(r.KeyDeleted ? "true" : "false").Append(',')
              .Append(Csv(r.ModelName)).Append(',')
              .Append(r.RequestCount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.PromptTokens.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.GeneratedTokens.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.TotalTokens.ToString(CultureInfo.InvariantCulture))
              .AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// RFC 4180 quoting, plus a leading apostrophe on values a spreadsheet would run as a formula
    /// (key and model names are admin-entered text, but still shouldn't execute in Excel).
    /// </summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
