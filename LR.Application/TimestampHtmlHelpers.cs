using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace LR.Application;

/// <summary>
/// Renders a stored-UTC timestamp as an HTML <c>&lt;time&gt;</c> element whose text the
/// client-side script in <c>_Layout.cshtml</c> rewrites into the viewer's chosen zone
/// (local time or UTC — see the "Timestamps" control in the sidebar).
/// The server-rendered text is always the UTC value, so the page still reads correctly
/// before the script runs or with JavaScript disabled.
/// </summary>
public static class TimestampHtmlHelpers
{
    /// <param name="format">
    /// One of <c>time</c> (HH:mm:ss), <c>date</c> (MM-dd), <c>date-full</c> (yyyy-MM-dd),
    /// <c>datetime</c> (yyyy-MM-dd HH:mm:ss) or <c>datetime-ms</c> (….fff). Defaults to <c>datetime</c>.
    /// </param>
    /// <param name="suffix">
    /// When true (the default), <c>datetime</c>/<c>datetime-ms</c> values get a trailing
    /// " UTC" / " local" marker so the displayed zone is unambiguous.
    /// </param>
    public static IHtmlContent Timestamp(this IHtmlHelper html, DateTimeOffset value, string format = "datetime", bool suffix = true)
        => Build(value.UtcDateTime, format, suffix);

    /// <inheritdoc cref="Timestamp(IHtmlHelper, DateTimeOffset, string, bool)"/>
    public static IHtmlContent Timestamp(this IHtmlHelper html, DateTime value, string format = "datetime", bool suffix = true)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            // Timestamps come back from SQLite as Unspecified; the app stores them in UTC.
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        return Build(utc, format, suffix);
    }

    private static IHtmlContent Build(DateTime utc, string format, bool suffix)
    {
        format = format switch
        {
            "time" or "date" or "date-full" or "datetime" or "datetime-ms" => format,
            _ => "datetime",
        };

        var withSuffix = suffix && format is "datetime" or "datetime-ms";

        var text = format switch
        {
            "time" => utc.ToString("HH:mm:ss"),
            "date" => utc.ToString("MM-dd"),
            "date-full" => utc.ToString("yyyy-MM-dd"),
            "datetime-ms" => utc.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            _ => utc.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        if (withSuffix) text += " UTC";

        var tag = new TagBuilder("time");
        tag.Attributes["datetime"] = utc.ToString("o");
        tag.Attributes["data-ts-format"] = format;
        if (withSuffix) tag.Attributes["data-ts-suffix"] = "true";
        tag.InnerHtml.Append(text);
        return tag;
    }
}
