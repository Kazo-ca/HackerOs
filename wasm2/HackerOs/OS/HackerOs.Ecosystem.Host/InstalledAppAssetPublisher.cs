using HackerOs.Platform.Blazor.LazyLoading;
using HackerOs.Platform.Core.Apps;
using HackerOs.Platform.Core.Apps.Packages;
using Microsoft.Extensions.Logging;

namespace HackerOs.Ecosystem;

/// <summary>
/// The cache a service worker serves installed apps' <c>wwwroot/</c> files from (ADR 0206). The
/// browser implementation is <c>BrowserInstalledAppAssets</c> (Cache Storage); tests use a dictionary.
/// Addresses are relative to the page's base: <c>_content/&lt;assembly&gt;/&lt;path&gt;</c>.
/// </summary>
public interface IInstalledAppAssetCache
{
    /// <summary>Makes sure a service worker controls the page; <see langword="false"/> when none can.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    ValueTask<bool> PrepareAsync(CancellationToken cancellationToken);

    /// <summary>Lists every cached address.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Stores one file.</summary>
    /// <param name="url">Its address.</param>
    /// <param name="bytes">Its verified bytes.</param>
    /// <param name="contentType">The <c>Content-Type</c> to answer with.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask PutAsync(string url, byte[] bytes, string contentType, CancellationToken cancellationToken);

    /// <summary>Removes one address.</summary>
    /// <param name="url">Its address.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    ValueTask RemoveAsync(string url, CancellationToken cancellationToken);
}

/// <summary>
/// Publishes the <c>wwwroot/</c> files of an installed app (ADR 0206) before its first launch in the
/// session: each file declared in its manifest is read through <see cref="AppAssetStore"/> (so its
/// SHA-256 is checked, ADR 0098 §5) and copied into the cache the service worker serves at
/// <c>_content/&lt;assembly&gt;/&lt;path&gt;</c>, the address Blazor gives a built-in app's files.
/// </summary>
/// <remarks>
/// Like a package manager unpacking files where the web server finds them, the copy is made once
/// per session per app, at its first launch. The first publication also drops every cached address
/// no installed app declares any more (an app removed by <c>hackpkg remove</c> or updated before the
/// restart). A damaged or missing file is left out, with a <c>/var/log/syslog</c> line; the launch
/// goes on, as with a built-in app's missing file.
/// </remarks>
public sealed class InstalledAppAssetPublisher
{
    private readonly InstalledAppsBoot _installed;
    private readonly IInstalledAppAssetCache _cache;
    private readonly Func<AppAssetStore> _store;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _published = new(StringComparer.Ordinal);
    private bool? _served;

    /// <summary>Creates the publisher.</summary>
    /// <param name="installed">The installed apps of this session.</param>
    /// <param name="cache">The cache the service worker serves.</param>
    /// <param name="store">Reads and checks the files (resolved late: it comes with the OS services).</param>
    /// <param name="logger">Receives the <c>/var/log/syslog</c> lines.</param>
    public InstalledAppAssetPublisher(InstalledAppsBoot installed, IInstalledAppAssetCache cache, Func<AppAssetStore> store, ILogger<InstalledAppAssetPublisher>? logger = null)
    {
        _installed = installed ?? throw new ArgumentNullException(nameof(installed));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
    }

    /// <summary>Gets the address a file of an installed app is served at.</summary>
    /// <param name="app">The installed app.</param>
    /// <param name="assetPath">The path declared in its manifest.</param>
    /// <returns>For example <c>_content/Kazo.Sample/engine/worker.js</c>.</returns>
    public static string GetUrl(InstalledApp app, string assetPath)
    {
        ArgumentNullException.ThrowIfNull(app);
        return StaticWebAssetAppAssetSource.RelativeUrl(app.Manifest, assetPath);
    }

    /// <summary>
    /// Publishes the files of <paramref name="app"/> unless it was done in this session. Never throws
    /// for a file or cache failure (it is logged); only <paramref name="cancellationToken"/> stops it.
    /// </summary>
    /// <param name="app">The installed app about to start.</param>
    /// <param name="cancellationToken">Cancels the publication.</param>
    public async Task PublishAsync(InstalledApp app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Manifest.Assets.Count == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_published.Contains(app.AppId))
            {
                return;
            }

            _served ??= await PrepareAsync(cancellationToken).ConfigureAwait(false);
            if (_served == true)
            {
                await CopyAsync(app, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger?.LogWarning("apps: no service worker serves this page, the wwwroot/ files of {AppId} are not served", app.AppId);
            }

            _published.Add(app.AppId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> PrepareAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await _cache.PrepareAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            await DropStaleAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(exception, "apps: the cache of installed apps' wwwroot/ files cannot be used");
            return false;
        }
    }

    private async Task DropStaleAsync(CancellationToken cancellationToken)
    {
        HashSet<string> declared = new(StringComparer.Ordinal);
        foreach (InstalledApp app in _installed.Current.Installed)
        {
            declared.UnionWith(app.Manifest.Assets.Select(asset => GetUrl(app, asset.Path)));
        }

        foreach (string url in await _cache.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!declared.Contains(url))
            {
                await _cache.RemoveAsync(url, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task CopyAsync(InstalledApp app, CancellationToken cancellationToken)
    {
        AppAssetStore store = _store();
        int copied = 0;
        foreach (string assetPath in app.Manifest.Assets.Select(static asset => asset.Path))
        {
            string url = GetUrl(app, assetPath);
            try
            {
                byte[] bytes = await store.ReadAsync(app.AppId, assetPath, cancellationToken).ConfigureAwait(false);
                await _cache.PutAsync(url, bytes, InstalledAppAssetContentTypes.For(assetPath), cancellationToken).ConfigureAwait(false);
                copied++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // AppAssetStore already logged a damaged file; a stale copy must not be served instead.
                _logger?.LogWarning("apps: {Url} of {AppId} is not served ({Reason})", url, app.AppId, exception.Message);
                await TryRemoveAsync(url, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger?.LogInformation("apps: {Count} wwwroot/ file(s) of {AppId} {Version} served", copied, app.AppId, app.Version);
    }

    private async Task TryRemoveAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            await _cache.RemoveAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(exception, "apps: {Url} could not be removed from the cache", url);
        }
    }
}

/// <summary>
/// The <c>Content-Type</c> of an installed app's file, from its extension (ADR 0206). The browser
/// needs the right one: a module script, a style sheet or <c>WebAssembly.instantiateStreaming</c>
/// refuses a wrong type.
/// </summary>
public static class InstalledAppAssetContentTypes
{
    /// <summary>The type of a file whose extension is not listed.</summary>
    public const string Default = "application/octet-stream";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".js"] = "text/javascript",
        [".mjs"] = "text/javascript",
        [".css"] = "text/css",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".json"] = "application/json",
        [".map"] = "application/json",
        [".wasm"] = "application/wasm",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
        [".mp3"] = "audio/mpeg",
        [".ogg"] = "audio/ogg",
        [".wav"] = "audio/wav",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".txt"] = "text/plain",
        [".xml"] = "application/xml",
    };

    /// <summary>Gets the type of a file.</summary>
    /// <param name="assetPath">Its declared path.</param>
    public static string For(string assetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);
        return ByExtension.TryGetValue(Path.GetExtension(assetPath), out string? type) ? type : Default;
    }
}
