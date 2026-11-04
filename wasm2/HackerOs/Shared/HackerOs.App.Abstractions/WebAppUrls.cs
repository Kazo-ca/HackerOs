using System.Globalization;

namespace HackerOs.App.Abstractions;

/// <summary>
/// The web part of a <see cref="AppKind.Web"/> app's manifest (ADR 0073, section 1), written as
/// <c>web</c> in <c>manifest.json</c>.
/// </summary>
/// <param name="Urls">
/// The URLs the site answers on, e.g. <c>http://intranet.hack/</c>: the OS resolver publishes their
/// host names (ADR 0074, section 4) and the site may listen on them without <c>network.listen</c>.
/// Normalized by <see cref="WebAppUrls.Normalize"/>: lower-case scheme and host, explicit path ending
/// with <c>/</c>, default port omitted.
/// </param>
/// <param name="Blazor">
/// The root component of the site's interactive Blazor part (ADR 0084), or <see langword="null"/>
/// when the site has none; omitted from <c>manifest.json</c> then.
/// </param>
public sealed record WebAppManifest(IReadOnlyList<string> Urls, WebBlazorManifest? Blazor = null);

/// <summary>
/// The <c>web.blazor</c> part of a web app's manifest (ADR 0084, section 1): where the root component
/// of its interactive Blazor part is.
/// </summary>
/// <param name="Assembly">Assembly file name, the entry point's (<c>HackerOs.Sites.Intranet.dll</c>).</param>
/// <param name="Type">Full name of the root component type.</param>
public sealed record WebBlazorManifest(string Assembly, string Type);

/// <summary>
/// One parsed HTTP binding: what an <c>HttpListener</c> prefix or a web app URL names (ADR 0074,
/// section 2). <c>https</c> is accepted and served as plain HTTP by the simulated network.
/// </summary>
/// <param name="Scheme"><c>http</c> or <c>https</c>.</param>
/// <param name="Host">
/// Lower-case host name or IPv4 literal, or a wildcard: <c>+</c> (strong, any host) or <c>*</c>
/// (weak, any host no other binding claims), as in <c>System.Net.HttpListener</c> prefixes.
/// </param>
/// <param name="Port">TCP port, 1 to 65535 (80 for <c>http</c>, 443 for <c>https</c> when omitted).</param>
/// <param name="Path">Absolute path prefix, always ending with <c>/</c>.</param>
public readonly record struct WebBinding(string Scheme, string Host, int Port, string Path)
{
    /// <summary>Gets whether the host is the strong (<c>+</c>) or weak (<c>*</c>) wildcard.</summary>
    public bool IsWildcard => Host is "+" or "*";

    /// <summary>Gets whether the host is <c>localhost</c> or a loopback literal (127.0.0.0/8).</summary>
    public bool IsLoopbackHost => WebAppUrls.IsLoopbackName(Host);

    /// <summary>Gets whether this binding needs <c>network.listen.privileged</c> (port below 1024).</summary>
    public bool IsPrivilegedPort => Port < WebAppUrls.FirstUnprivilegedPort;

    /// <summary>Gets the binding back as a URL: <c>scheme://host[:port]/path</c>, default port omitted.</summary>
    public override string ToString() =>
        Port == WebAppUrls.DefaultPort(Scheme)
            ? $"{Scheme}://{Host}{Path}"
            : string.Create(CultureInfo.InvariantCulture, $"{Scheme}://{Host}:{Port}{Path}");
}

/// <summary>
/// Parsing and normalization of web URLs and listener prefixes (ADR 0073 and 0074), shared by the
/// manifest validator, the SDK listener and the OS port table so all three agree on what a URL means.
/// </summary>
public static class WebAppUrls
{
    /// <summary>The first port an unprivileged process may bind, as on Linux (<c>CAP_NET_BIND_SERVICE</c>).</summary>
    public const int FirstUnprivilegedPort = 1024;

    /// <summary>The internal top-level domain of Kazo OS sites (ADR 0074, section 4): never relayed to the real Internet.</summary>
    public const string InternalDomain = "hack";

    /// <summary>Gets the default port of <paramref name="scheme"/>: 443 for <c>https</c>, otherwise 80.</summary>
    /// <param name="scheme">A lower-case scheme.</param>
    public static int DefaultPort(string scheme) =>
        string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;

    /// <summary>
    /// Parses an absolute web URL or listener prefix: <c>http</c> or <c>https</c>, a host name, an
    /// IPv4 literal or (when <paramref name="allowWildcardHost"/>) <c>+</c>/<c>*</c>, an optional
    /// port, and a path that must end with <c>/</c>. No user info, query or fragment.
    /// </summary>
    /// <param name="value">The URL or prefix.</param>
    /// <param name="allowWildcardHost">Whether <c>+</c> and <c>*</c> are accepted as host (listener prefixes only).</param>
    /// <param name="binding">The parsed binding.</param>
    /// <param name="error">Why the value was rejected; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when the value is valid.</returns>
    public static bool TryParse(string? value, bool allowWildcardHost, out WebBinding binding, out string? error)
    {
        binding = default;
        error = null;
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length != value.Length)
        {
            error = "a web URL cannot be empty or padded with spaces";
            return false;
        }

        int schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        string scheme = schemeEnd < 0 ? string.Empty : value[..schemeEnd].ToLowerInvariant();
        if (scheme is not ("http" or "https"))
        {
            error = $"'{value}' must start with http:// or https://";
            return false;
        }

        string rest = value[(schemeEnd + 3)..];
        int pathStart = rest.IndexOf('/', StringComparison.Ordinal);
        if (pathStart < 0)
        {
            error = $"'{value}' needs a path ending with '/', e.g. '{value}/'";
            return false;
        }

        string authority = rest[..pathStart];
        string path = rest[pathStart..];
        if (authority.Contains('@', StringComparison.Ordinal) || path.IndexOfAny(['?', '#']) >= 0)
        {
            error = $"'{value}' cannot carry user info, a query or a fragment";
            return false;
        }

        if (!path.EndsWith('/') || path.Contains("//", StringComparison.Ordinal) || path.Contains(' ', StringComparison.Ordinal))
        {
            error = $"the path of '{value}' must end with '/' and contain no empty segment";
            return false;
        }

        string host = authority;
        int port = DefaultPort(scheme);
        int colon = authority.LastIndexOf(':');
        if (colon >= 0)
        {
            host = authority[..colon];
            if (!int.TryParse(authority[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
            {
                error = $"the port of '{value}' must be a number from 1 to 65535";
                return false;
            }
        }

        host = host.ToLowerInvariant();
        bool wildcard = host is "+" or "*";
        if (wildcard && !allowWildcardHost)
        {
            error = $"'{value}' must name a host: wildcards are for listener prefixes only";
            return false;
        }

        if (!wildcard && !IsValidHostName(host))
        {
            error = $"'{host}' is not a valid host name";
            return false;
        }

        binding = new WebBinding(scheme, host, port, path);
        return true;
    }

    /// <summary>Parses a value <see cref="TryParse"/> accepts, or throws.</summary>
    /// <param name="value">The URL or prefix.</param>
    /// <param name="allowWildcardHost">Whether <c>+</c> and <c>*</c> are accepted as host.</param>
    /// <returns>The binding.</returns>
    /// <exception cref="FormatException">The value is not a valid web URL or prefix.</exception>
    public static WebBinding Parse(string value, bool allowWildcardHost) =>
        TryParse(value, allowWildcardHost, out WebBinding binding, out string? error)
            ? binding
            : throw new FormatException(error);

    /// <summary>
    /// Normalizes a valid URL (lower-case scheme and host, default port omitted); returns the value
    /// unchanged when it is invalid, so the validator can still report it.
    /// </summary>
    /// <param name="value">The URL.</param>
    public static string Normalize(string value) =>
        TryParse(value, allowWildcardHost: false, out WebBinding binding, out _) ? binding.ToString() : value;

    /// <summary>Gets whether <paramref name="host"/> is <c>localhost</c>, a name under it, or an address of 127.0.0.0/8.</summary>
    /// <param name="host">A host name or literal.</param>
    public static bool IsLoopbackName(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || (System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? address) && System.Net.IPAddress.IsLoopback(address));

    /// <summary>Gets whether <paramref name="host"/> is in the internal <c>.hack</c> domain (never relayed to the Internet).</summary>
    /// <param name="host">A host name.</param>
    public static bool IsInternalDomain(string host) =>
        host.Equals(InternalDomain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + InternalDomain, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets whether <paramref name="host"/> is a lower-case DNS name (labels of letters, digits and
    /// inner hyphens, at most 63 characters each, 253 in all) or an IPv4 literal.
    /// </summary>
    /// <param name="host">The candidate host.</param>
    public static bool IsValidHostName(string host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 253)
        {
            return false;
        }

        if (System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? address))
        {
            return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && host.Count(c => c == '.') == 3;
        }

        foreach (string label in host.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-'
                || !label.All(static c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
