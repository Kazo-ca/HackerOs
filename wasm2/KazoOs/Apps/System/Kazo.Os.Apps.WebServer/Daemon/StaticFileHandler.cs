using System.Globalization;
using System.Net;
using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.AppSdk.Net.Hosting;
using Kazo.Os.Simulation.Abstractions.Network;

namespace Kazo.Os.Apps.WebServer.Daemon;

/// <summary>One site as <c>kazowebd</c> serves it on one binding.</summary>
/// <param name="Declaration">The site's <c>.site</c> file.</param>
/// <param name="Binding">The binding the request came in on (its path is the site's base path).</param>
public sealed record ServedSite(KazoWebSiteDeclaration Declaration, WebBinding Binding)
{
    /// <summary>Gets the site.</summary>
    public KazoWebSite Site => Declaration.Site!;

    /// <summary>Gets whether files are read as <c>www-data</c> (a system site) rather than as the declaring user.</summary>
    public bool ServesAsWwwData => Declaration.Scope == KazoWebSiteScope.System;
}

/// <summary>What a request produced, for the access log.</summary>
/// <param name="StatusCode">The status code sent.</param>
/// <param name="BodyBytes">Bytes of body sent.</param>
/// <param name="Error">Why it failed, for the error log; <see langword="null"/> when nothing is worth logging.</param>
public readonly record struct StaticFileResult(int StatusCode, long BodyBytes, string? Error = null);

/// <summary>
/// Serves one request of a static site the way Apache, nginx and IIS's static-file handler do
/// (ADR 0077, section 2): <c>GET</c>/<c>HEAD</c> only, the URL mapped under the site root without ever
/// leaving it, a <c>301</c> to add the slash of a directory, default documents, an optional "Index
/// of" listing, a <c>Content-Type</c> from the extension, <c>ETag</c>/<c>Last-Modified</c> validators and
/// their <c>304</c>, and plain <c>403</c>/<c>404</c>/<c>405</c> pages.
/// </summary>
public static class StaticFileHandler
{
    /// <summary>Methods a static site answers.</summary>
    public const string AllowedMethods = "GET, HEAD, OPTIONS";

    /// <summary>Handles one request and fills its response (the caller closes it).</summary>
    /// <param name="site">The site and binding the request came in on.</param>
    /// <param name="context">The request and its response.</param>
    /// <param name="cancellationToken">Cancels the file reads.</param>
    public static async Task<StaticFileResult> HandleAsync(ServedSite site, KazoHttpListenerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(context);
        KazoHttpListenerRequest request = context.Request;
        KazoHttpListenerResponse response = context.Response;
        response.AddHeader("Server", KazoWebdLog.ServerSignature);

        switch (request.HttpMethod)
        {
            case "GET" or "HEAD":
                break;
            case "OPTIONS":
                response.StatusCode = 200;
                response.AddHeader("Allow", AllowedMethods);
                return new StaticFileResult(200, 0);
            default:
                response.AddHeader("Allow", AllowedMethods);
                return ErrorPage(response, 405, "Method Not Allowed", request);
        }

        if (!TryMapPath(site, request.Url, out string? path))
        {
            return ErrorPage(response, 400, "Bad Request", request, $"refused URL \"{request.RawUrl}\" (outside the site root)");
        }

        KazoDirectoryInfo directory;
        try
        {
            directory = await KazoDirectoryInfo.GetAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            // The server's own user may not even stat the entry: nginx answers 403 to EACCES.
            return ErrorPage(response, 403, "Forbidden", request, $"stat() \"{path}\" failed (13: Permission denied)");
        }

        if (directory.Exists)
        {
            return await ServeDirectoryAsync(site, context, path, cancellationToken).ConfigureAwait(false);
        }

        return await ServeFileAsync(site, context, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Maps a request URL to a path under the site root: the binding's path is removed, the rest is
    /// URL-decoded, and any <c>..</c> segment, backslash or NUL refuses the request (no escape from
    /// the root, the bug every web server once had).
    /// </summary>
    /// <param name="site">The site.</param>
    /// <param name="url">The request URL.</param>
    /// <param name="path">The absolute virtual path.</param>
    public static bool TryMapPath(ServedSite site, Uri url, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? path)
    {
        path = null;
        string absolute;
        try
        {
            absolute = Uri.UnescapeDataString(url.AbsolutePath);
        }
        catch (UriFormatException)
        {
            return false;
        }

        string basePath = site.Binding.Path;
        string relative = absolute.StartsWith(basePath, StringComparison.Ordinal)
            ? absolute[basePath.Length..]
            : absolute.TrimStart('/');
        if (relative.Contains('\\', StringComparison.Ordinal) || relative.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        List<string> segments = [];
        foreach (string segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                return false;
            }

            if (segment != ".")
            {
                segments.Add(segment);
            }
        }

        string root = site.Site.Root.TrimEnd('/');
        path = segments.Count == 0 ? root : root + "/" + string.Join('/', segments);
        return true;
    }

    private static async Task<StaticFileResult> ServeDirectoryAsync(
        ServedSite site, KazoHttpListenerContext context, string path, CancellationToken cancellationToken)
    {
        KazoHttpListenerRequest request = context.Request;
        KazoHttpListenerResponse response = context.Response;
        if (!request.Url.AbsolutePath.EndsWith('/'))
        {
            // Like Apache's mod_dir: relative links of index.html only work under the trailing slash.
            string location = request.Url.AbsolutePath + "/" + request.Url.Query;
            response.RedirectLocation = location;
            response.StatusCode = 301;
            return WriteHtml(response, 301, StatusPage(301, "Moved Permanently"), request);
        }

        foreach (string document in site.Site.DefaultDocuments)
        {
            string candidate = path + "/" + document;
            KazoFileInfo file = await KazoFileInfo.GetAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (file.Exists)
            {
                return await ServeFileAsync(site, context, candidate, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!site.Site.DirectoryBrowsing)
        {
            return ErrorPage(response, 403, "Forbidden", request, $"directory index of \"{path}/\" is forbidden");
        }

        if (site.ServesAsWwwData && !await WwwDataAccess.CanListDirectoryAsync(path, cancellationToken).ConfigureAwait(false))
        {
            return ErrorPage(response, 403, "Forbidden", request, $"opendir() \"{path}\" failed (13: Permission denied for {WwwDataAccess.AccountName})");
        }

        KazoFileSystemInfo[] entries;
        try
        {
            entries = await new KazoDirectoryInfo(path).GetFileSystemInfosAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            return ErrorPage(response, 403, "Forbidden", request, $"opendir() \"{path}\" failed (13: Permission denied)");
        }

        string html = DirectoryListing(Uri.UnescapeDataString(request.Url.AbsolutePath), entries);
        return WriteHtml(response, 200, html, request);
    }

    private static async Task<StaticFileResult> ServeFileAsync(
        ServedSite site, KazoHttpListenerContext context, string path, CancellationToken cancellationToken)
    {
        KazoHttpListenerRequest request = context.Request;
        KazoHttpListenerResponse response = context.Response;
        KazoFileInfo file;
        try
        {
            file = await KazoFileInfo.GetAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            return ErrorPage(response, 403, "Forbidden", request, $"stat() \"{path}\" failed (13: Permission denied)");
        }

        if (!file.Exists)
        {
            return ErrorPage(response, 404, "Not Found", request, $"open() \"{path}\" failed (2: No such file or directory)");
        }

        if (site.ServesAsWwwData && !await WwwDataAccess.CanReadFileAsync(path, cancellationToken).ConfigureAwait(false))
        {
            return ErrorPage(response, 403, "Forbidden", request, $"open() \"{path}\" failed (13: Permission denied for {WwwDataAccess.AccountName})");
        }

        string etag = $"\"{(file.Revision ?? file.LastWriteTimeUtc.Ticks).ToString("x", CultureInfo.InvariantCulture)}-{file.Length.ToString("x", CultureInfo.InvariantCulture)}\"";
        DateTime lastModified = TruncateToSeconds(file.LastWriteTimeUtc);
        response.AddHeader("ETag", etag);
        response.AddHeader("Last-Modified", lastModified.ToString("r", CultureInfo.InvariantCulture));
        response.AddHeader("Accept-Ranges", "none");
        if (IsNotModified(request, etag, lastModified))
        {
            response.StatusCode = 304;
            return new StaticFileResult(304, 0);
        }

        byte[] content;
        try
        {
            content = await KazoFile.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            return ErrorPage(response, 403, "Forbidden", request, $"open() \"{path}\" failed (13: Permission denied)");
        }

        response.StatusCode = 200;
        response.ContentType = SimulatedFileTypes.GetContentType(path);
        if (request.HttpMethod == "HEAD")
        {
            response.AddHeader("Content-Length", content.Length.ToString(CultureInfo.InvariantCulture));
            return new StaticFileResult(200, 0);
        }

        await response.OutputStream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        return new StaticFileResult(200, content.Length);
    }

    /// <summary>
    /// Applies the conditional-request rules of RFC 9110 §13.2.2: <c>If-None-Match</c> wins when
    /// present (<c>*</c> or a matching tag); otherwise <c>If-Modified-Since</c> at the second.
    /// </summary>
    private static bool IsNotModified(KazoHttpListenerRequest request, string etag, DateTime lastModified)
    {
        if (request.Headers["If-None-Match"] is { Length: > 0 } ifNoneMatch)
        {
            return ifNoneMatch.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(tag => tag == "*" || tag == etag || tag == "W/" + etag);
        }

        return request.Headers["If-Modified-Since"] is { Length: > 0 } ifModifiedSince
            && DateTimeOffset.TryParseExact(ifModifiedSince, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset since)
            && lastModified <= since.UtcDateTime;
    }

    private static DateTime TruncateToSeconds(DateTime value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

    private static StaticFileResult ErrorPage(KazoHttpListenerResponse response, int status, string reason, KazoHttpListenerRequest request, string? error = null)
    {
        StaticFileResult written = WriteHtml(response, status, StatusPage(status, reason), request);
        return written with { Error = error };
    }

    private static StaticFileResult WriteHtml(KazoHttpListenerResponse response, int status, string html, KazoHttpListenerRequest request)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=utf-8";
        byte[] body = Encoding.UTF8.GetBytes(html);
        if (request.HttpMethod == "HEAD")
        {
            return new StaticFileResult(status, 0);
        }

        response.OutputStream.Write(body);
        return new StaticFileResult(status, body.Length);
    }

    /// <summary>The error page nginx prints, signed by <c>kazowebd</c>.</summary>
    /// <param name="status">The status code.</param>
    /// <param name="reason">Its reason phrase.</param>
    public static string StatusPage(int status, string reason) =>
        $"<html>\n<head><title>{status} {reason}</title></head>\n<body>\n<center><h1>{status} {reason}</h1></center>\n" +
        $"<hr><center>{KazoWebdLog.ServerSignature}</center>\n</body>\n</html>\n";

    /// <summary>An "Index of" page like nginx's <c>autoindex</c>: directories first, then files, with size and date.</summary>
    /// <param name="urlPath">The decoded URL path of the directory (ends with <c>/</c>).</param>
    /// <param name="entries">The directory's entries.</param>
    public static string DirectoryListing(string urlPath, IEnumerable<KazoFileSystemInfo> entries)
    {
        string title = WebUtility.HtmlEncode($"Index of {urlPath}");
        StringBuilder html = new();
        html.Append("<html>\n<head><title>").Append(title).Append("</title></head>\n<body>\n<h1>").Append(title).Append("</h1><hr><pre>");
        if (urlPath != "/")
        {
            html.Append("<a href=\"../\">../</a>\n");
        }

        foreach (KazoFileSystemInfo entry in entries
            .OrderBy(static entry => entry is KazoFileInfo)
            .ThenBy(static entry => entry.Name, StringComparer.Ordinal))
        {
            bool isDirectory = entry is KazoDirectoryInfo;
            string name = entry.Name + (isDirectory ? "/" : string.Empty);
            string href = Uri.EscapeDataString(entry.Name) + (isDirectory ? "/" : string.Empty);
            string size = entry is KazoFileInfo file ? file.Length.ToString(CultureInfo.InvariantCulture) : "-";
            string date = entry.LastWriteTimeUtc.ToString("dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture);
            string shown = name.Length > 50 ? name[..47] + "..>" : name;
            html.Append("<a href=\"").Append(href).Append("\">").Append(WebUtility.HtmlEncode(shown)).Append("</a>")
                .Append(' ', Math.Max(1, 51 - shown.Length)).Append(date).Append(' ', 1).Append(size.PadLeft(19)).Append('\n');
        }

        html.Append("</pre><hr></body>\n</html>\n");
        return html.ToString();
    }
}
