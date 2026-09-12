namespace LR.Application.Pages.Features.Models;

/// <summary>
/// Small display-formatting helpers shared between the Models index page and its per-row partial.
/// </summary>
public static class ModelDisplayFormat
{
    public static string Truncate(string? s, int max)
        => s is not null && s.Length > max ? s[..(max - 3)] + "..." : (s ?? "");

    public static string FormatSize(long? bytes)
    {
        if (bytes is null) return "—";
        double gb = bytes.Value / 1024.0 / 1024.0 / 1024.0;
        return gb >= 1 ? $"{gb:F1} GB" : $"{bytes.Value / 1024.0 / 1024.0:F0} MB";
    }
}
