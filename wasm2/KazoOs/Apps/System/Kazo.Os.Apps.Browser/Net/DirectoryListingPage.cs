using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Browser.Net;

/// <summary>One entry of a directory listing.</summary>
/// <param name="Name">File or directory name.</param>
/// <param name="IsDirectory">Whether it is a directory.</param>
/// <param name="Length">Size in bytes of a file; <see langword="null"/> for a directory.</param>
/// <param name="LastWriteTimeUtc">Last modification time.</param>
public sealed record DirectoryListingEntry(string Name, bool IsDirectory, long? Length, DateTime LastWriteTimeUtc);

/// <summary>
/// The "Index of /path/" page a browser generates when <c>file://</c> points at a directory, as
/// Chromium does. The page is ordinary HTML, rendered in the isolated frame like any other. Its
/// texts follow the language of the browser's process (ADR 0104); names and dates are not translated.
/// </summary>
public static class DirectoryListingPage
{
    /// <summary>Builds the listing of <paramref name="directoryPath"/>: directories first, then files, by name.</summary>
    /// <param name="directoryPath">The directory listed.</param>
    /// <param name="entries">Its files and directories.</param>
    /// <param name="culture">The language of the page's texts; <see langword="null"/> gives English.</param>
    /// <param name="formatCulture">How sizes are written; <see langword="null"/> gives the invariant format.</param>
    public static string Build(string directoryPath, IEnumerable<DirectoryListingEntry> entries, CultureInfo? culture = null, CultureInfo? formatCulture = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        IStringLocalizer<Strings> text = Strings.For(culture);
        string path = directoryPath.EndsWith('/') ? directoryPath : directoryPath + "/";
        string title = WebUtility.HtmlEncode(text["Listing.Title", path]);

        StringBuilder html = new();
        html.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>").Append(title).Append("</title>")
            .Append("<style>")
            .Append("body{background:#0b0f14;color:#c9d1d9;font:14px/1.5 'Cascadia Code',Consolas,monospace;margin:24px}")
            .Append("h1{color:#39ff14;font-size:18px;border-bottom:1px solid #1f2a36;padding-bottom:8px}")
            .Append("table{border-collapse:collapse;min-width:60%}td,th{padding:2px 24px 2px 0;text-align:left}")
            .Append("th{color:#8b949e;font-weight:normal}a{color:#58a6ff;text-decoration:none}a:hover{text-decoration:underline}")
            .Append(".size,.date{color:#8b949e}")
            .Append("</style></head><body><h1>").Append(title).Append("</h1><table>")
            .Append("<tr><th>").Append(WebUtility.HtmlEncode(text["Listing.Name"]))
            .Append("</th><th>").Append(WebUtility.HtmlEncode(text["Listing.Size"]))
            .Append("</th><th>").Append(WebUtility.HtmlEncode(text["Listing.DateModified"])).Append("</th></tr>");

        if (path != "/")
        {
            html.Append("<tr><td><a href=\"../\">").Append(WebUtility.HtmlEncode(text["Listing.Parent"])).Append("</a></td><td></td><td></td></tr>");
        }

        foreach (DirectoryListingEntry entry in entries
                     .OrderByDescending(entry => entry.IsDirectory)
                     .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            string href = Uri.EscapeDataString(entry.Name) + (entry.IsDirectory ? "/" : string.Empty);
            string label = WebUtility.HtmlEncode(entry.Name + (entry.IsDirectory ? "/" : string.Empty));
            html.Append("<tr><td><a href=\"").Append(href).Append("\">").Append(label).Append("</a></td>")
                .Append("<td class=\"size\">").Append(entry.Length is { } length ? FormatSize(length, culture, formatCulture) : string.Empty).Append("</td>")
                .Append("<td class=\"date\">")
                .Append(entry.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                .Append("</td></tr>");
        }

        return html.Append("</table></body></html>").ToString();
    }

    /// <summary>
    /// Formats a size as Chromium's listing does (<c>12 B</c>, <c>3.4 kB</c>, <c>1.2 MB</c>; in French
    /// <c>3,4 ko</c>).
    /// </summary>
    /// <param name="bytes">The size.</param>
    /// <param name="culture">The language of the unit; <see langword="null"/> gives English.</param>
    /// <param name="formatCulture">How the number is written; <see langword="null"/> gives the invariant format.</param>
    public static string FormatSize(long bytes, CultureInfo? culture = null, CultureInfo? formatCulture = null)
    {
        IStringLocalizer<Strings> text = Strings.For(culture);
        CultureInfo numbers = formatCulture ?? CultureInfo.InvariantCulture;
        return bytes switch
        {
            < 1000 => text["Size.Bytes", bytes.ToString(numbers)],
            < 1000 * 1000 => text["Size.Kilobytes", (bytes / 1000.0).ToString("0.#", numbers)],
            _ => text["Size.Megabytes", (bytes / (1000.0 * 1000)).ToString("0.#", numbers)],
        };
    }
}
