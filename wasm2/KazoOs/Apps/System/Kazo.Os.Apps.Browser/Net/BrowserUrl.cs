namespace Kazo.Os.Apps.Browser.Net;

/// <summary>What the address bar resolved a typed text to.</summary>
/// <param name="Url">The URL to load.</param>
/// <param name="ViewSource">The user typed <c>view-source:</c>: show the response as source.</param>
public sealed record AddressTarget(Uri Url, bool ViewSource);

/// <summary>
/// URL rules of the browser, pure so they are unit-tested: what the omnibox does with typed text,
/// how a page's links and resources resolve, and which origin a URL belongs to (ADR 0075).
/// </summary>
public static class BrowserUrl
{
    /// <summary>The page shown by <c>about:blank</c>.</summary>
    public static readonly Uri AboutBlank = new("about:blank");

    private const string ViewSourcePrefix = "view-source:";

    /// <summary>
    /// Turns what the user typed into a URL, as Chromium's omnibox does: a full URL is kept; a path
    /// (<c>/home/…</c> or <c>~/…</c>) becomes a <c>file://</c> URL; a host name gets <c>https://</c>
    /// (<c>http://</c> for <c>localhost</c> and IP addresses); anything else is searched on
    /// <paramref name="searchUrl"/>.
    /// </summary>
    /// <param name="input">Typed text.</param>
    /// <param name="homeDirectory">Home directory used to expand <c>~</c>.</param>
    /// <param name="searchUrl">Search page, the query is appended URL-encoded.</param>
    /// <returns>The target, or <see langword="null"/> when the text is empty.</returns>
    public static AddressTarget? FromAddressBar(string? input, string homeDirectory, string searchUrl)
    {
        string text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        bool viewSource = false;
        if (text.StartsWith(ViewSourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            viewSource = true;
            text = text[ViewSourcePrefix.Length..].Trim();
            if (text.Length == 0)
            {
                return null;
            }
        }

        if (text.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return new AddressTarget(AboutBlank, viewSource);
        }

        if (text.StartsWith('/') || text == "~" || text.StartsWith("~/", StringComparison.Ordinal))
        {
            string path = text.StartsWith('~') ? homeDirectory.TrimEnd('/') + text[1..] : text;
            return new AddressTarget(FromLocalPath(path), viewSource);
        }

        if (HasScheme(text, "http") || HasScheme(text, "https") || HasScheme(text, "file"))
        {
            return Uri.TryCreate(text, UriKind.Absolute, out Uri? absolute)
                ? new AddressTarget(absolute, viewSource)
                : null;
        }

        // "word word" or a single word with no dot is a search, as in Chromium.
        bool looksLikeHost = !text.Contains(' ', StringComparison.Ordinal)
            && (text.Contains('.', StringComparison.Ordinal)
                || text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase));
        if (!looksLikeHost)
        {
            return new AddressTarget(new Uri(searchUrl + Uri.EscapeDataString(text)), viewSource);
        }

        string host = text.Split('/', ':')[0];
        string scheme = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || System.Net.IPAddress.TryParse(host, out _)
            ? "http"
            : "https";
        return Uri.TryCreate($"{scheme}://{text}", UriKind.Absolute, out Uri? url)
            ? new AddressTarget(url, viewSource)
            : null;
    }

    /// <summary>Builds the <c>file://</c> URL of an absolute OS path, escaping each segment.</summary>
    public static Uri FromLocalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string escaped = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        return new Uri("file://" + (escaped.StartsWith('/') ? escaped : "/" + escaped));
    }

    /// <summary>Gets the OS path of a <c>file://</c> URL, unescaped.</summary>
    public static string ToLocalPath(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        string path = Uri.UnescapeDataString(url.AbsolutePath);
        return path.Length > 1 ? path.TrimEnd('/') : "/";
    }

    /// <summary>
    /// Resolves a reference found in a page (<c>href</c>, <c>src</c>, <c>url()</c>) against the
    /// document's base URL. Returns <see langword="null"/> for references that are not loaded over
    /// the network: empty, fragments, <c>data:</c>, <c>blob:</c>, <c>javascript:</c>, <c>mailto:</c>…
    /// </summary>
    public static Uri? Resolve(Uri baseUrl, string? reference)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        string value = (reference ?? string.Empty).Trim();
        if (value.Length == 0 || value.StartsWith('#'))
        {
            return null;
        }

        int colon = value.IndexOf(':');
        int slash = value.IndexOf('/');
        if (colon > 0 && (slash < 0 || colon < slash))
        {
            string scheme = value[..colon].ToLowerInvariant();
            if (scheme is not ("http" or "https" or "file"))
            {
                return null;
            }
        }

        return Uri.TryCreate(baseUrl, value, out Uri? resolved)
            && resolved.Scheme is "http" or "https" or "file"
            ? resolved
            : null;
    }

    /// <summary>
    /// Gets the serialized origin of a URL, as <c>location.origin</c>: <c>https://site.hack</c>,
    /// <c>http://localhost:8080</c>; <c>null</c> for <c>file://</c> and <c>about:</c>, which have
    /// opaque origins.
    /// </summary>
    public static string GetOrigin(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return url.Scheme is "http" or "https"
            ? url.GetLeftPart(UriPartial.Authority).ToLowerInvariant()
            : "null";
    }

    /// <summary>Gets whether two URLs share an origin (scheme, host and port).</summary>
    public static bool IsSameOrigin(Uri first, Uri second)
    {
        string origin = GetOrigin(first);
        return origin != "null" && origin == GetOrigin(second);
    }

    /// <summary>
    /// Gets the key under which the browser keeps a page's <c>localStorage</c>: the host and port of
    /// a web page, <c>file</c> for every local file (Chromium shares one store for <c>file://</c>).
    /// </summary>
    public static string GetStorageKey(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme == "file")
        {
            return "file";
        }

        string host = url.Host.ToLowerInvariant();
        return url.IsDefaultPort ? $"{url.Scheme}_{host}" : $"{url.Scheme}_{host}_{url.Port}";
    }

    /// <summary>Gets the text the address bar shows for a URL (unescaped path for <c>file://</c>).</summary>
    public static string ToDisplayString(Uri url, bool viewSource = false)
    {
        ArgumentNullException.ThrowIfNull(url);
        string text = url.Scheme == "file" ? "file://" + Uri.UnescapeDataString(url.AbsolutePath) : url.AbsoluteUri;
        return viewSource ? ViewSourcePrefix + text : text;
    }

    private static bool HasScheme(string text, string scheme) =>
        text.StartsWith(scheme + "://", StringComparison.OrdinalIgnoreCase);
}
