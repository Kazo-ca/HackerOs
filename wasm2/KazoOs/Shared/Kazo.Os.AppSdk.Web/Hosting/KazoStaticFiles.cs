using System.Reflection;
using System.Security.Cryptography;
using Kazo.Os.AppSdk.Web.Http;
using Kazo.Os.AppSdk.Web.Routing;
using Kazo.Os.Simulation.Abstractions.Network;

namespace Kazo.Os.AppSdk.Web.Hosting;

/// <summary>Settings of <see cref="KazoWebApplication.UseStaticFiles(Action{KazoStaticFileOptions}?)"/>.</summary>
public sealed class KazoStaticFileOptions
{
    /// <summary>Gets or sets the assembly whose embedded <c>wwwroot/</c> is served; the site definition's by default.</summary>
    public Assembly? Assembly { get; set; }

    /// <summary>Gets or sets the prefix of the embedded resource names (<c>wwwroot/</c>, as the build writes them).</summary>
    public string ResourcePrefix { get; set; } = "wwwroot/";

    /// <summary>Gets or sets the URL path the files are served under (<c>/</c> by default; <c>/static</c> serves <c>wwwroot/css/site.css</c> at <c>/static/css/site.css</c>).</summary>
    public string RequestPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the files served for a directory (<c>/</c> → <c>index.html</c>); empty disables it.</summary>
    public IList<string> DefaultFileNames { get; } = ["index.html", "index.htm"];

    /// <summary>Gets or sets the <c>Cache-Control</c> header (<c>public, max-age=3600</c> by default; <see langword="null"/> writes none).</summary>
    public string? CacheControl { get; set; } = "public, max-age=3600";
}

/// <summary>
/// Serves the files the build embedded from the project's <c>wwwroot/</c> (ADR 0073 §2, ADR 0083 §6):
/// read-only, with the <c>Content-Type</c> of the extension, an <c>ETag</c>, <c>304</c> for a matching
/// <c>If-None-Match</c>, and <c>405</c> for a method other than <c>GET</c> or <c>HEAD</c>. A request no
/// file matches continues down the pipeline (to the controllers), like ASP.NET's <c>UseStaticFiles</c>.
/// </summary>
/// <remarks>
/// Resource names are the file paths under <c>wwwroot/</c> with <c>/</c> separators, compared
/// case-sensitively as a Linux web root; the names are read once at start-up, so no reflection runs
/// per request and nothing but the embedded files can be reached (<c>..</c> never matches a name).
/// </remarks>
public sealed class KazoStaticFileMiddleware : IKazoMiddleware
{
    private readonly KazoStaticFileOptions _options;
    private readonly Assembly _assembly;
    private readonly Dictionary<string, string> _resources;
    private readonly Dictionary<string, byte[]> _cache = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Initializes the middleware for one assembly.</summary>
    /// <param name="options">The options; <see cref="KazoStaticFileOptions.Assembly"/> must be set.</param>
    public KazoStaticFileMiddleware(KazoStaticFileOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _assembly = options.Assembly ?? throw new ArgumentException("The assembly of the static files is not set.", nameof(options));
        _resources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in _assembly.GetManifestResourceNames())
        {
            if (name.StartsWith(options.ResourcePrefix, StringComparison.Ordinal))
            {
                _resources[name[options.ResourcePrefix.Length..]] = name;
            }
        }
    }

    /// <summary>Gets the relative paths of the files served (<c>css/site.css</c>).</summary>
    public IReadOnlyCollection<string> Files => _resources.Keys;

    /// <inheritdoc />
    public async Task InvokeAsync(KazoHttpContext context, KazoRequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (TryResolve(context.Request.Path) is not string path)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        KazoHttpResponse response = context.Response;
        if (context.Request.Method is not ("GET" or "HEAD"))
        {
            response.StatusCode = 405;
            response.Headers["Allow"] = "GET, HEAD";
            return;
        }

        byte[] content = Read(path);
        string etag = $"\"{Convert.ToHexStringLower(SHA256.HashData(content).AsSpan(0, 16))}\"";
        response.Headers["ETag"] = etag;
        if (_options.CacheControl is string cacheControl)
        {
            response.Headers["Cache-Control"] = cacheControl;
        }

        if (context.Request.Headers["If-None-Match"] is string ifNoneMatch
            && ifNoneMatch.Split(',').Any(candidate => candidate.Trim() == etag || candidate.Trim() == "*"))
        {
            response.StatusCode = 304;
            return;
        }

        response.StatusCode = 200;
        response.ContentType = SimulatedFileTypes.GetContentType(path);
        if (context.Request.Method == "GET")
        {
            await response.Body.WriteAsync(content, context.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <summary>Finds the embedded file a request path names, or a default file of a directory.</summary>
    /// <param name="requestPath">The decoded request path (<c>/css/site.css</c>).</param>
    /// <returns>The relative resource path, or <see langword="null"/>.</returns>
    public string? TryResolve(string requestPath)
    {
        ArgumentNullException.ThrowIfNull(requestPath);
        string prefix = _options.RequestPath.TrimEnd('/');
        if (prefix.Length > 0)
        {
            if (!requestPath.StartsWith(prefix, StringComparison.Ordinal)
                || (requestPath.Length > prefix.Length && requestPath[prefix.Length] != '/'))
            {
                return null;
            }

            requestPath = requestPath[prefix.Length..];
        }

        string relative = requestPath.TrimStart('/');
        if (relative.Length > 0 && !relative.EndsWith('/') && _resources.ContainsKey(relative))
        {
            return relative;
        }

        if (relative.Length == 0 || relative.EndsWith('/'))
        {
            foreach (string defaultFile in _options.DefaultFileNames)
            {
                string candidate = relative + defaultFile;
                if (_resources.ContainsKey(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private byte[] Read(string path)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(path, out byte[]? cached))
            {
                return cached;
            }
        }

        using Stream stream = _assembly.GetManifestResourceStream(_resources[path])
            ?? throw new InvalidOperationException($"The embedded file '{path}' disappeared.");
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        byte[] content = buffer.ToArray();
        lock (_gate)
        {
            _cache[path] = content;
        }

        return content;
    }
}
