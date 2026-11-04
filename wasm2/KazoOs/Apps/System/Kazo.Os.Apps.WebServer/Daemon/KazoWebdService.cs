using System.Threading.Channels;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.AppSdk.Net.Hosting;
using Kazo.Os.Simulation.Abstractions.Diagnostics;

namespace Kazo.Os.Apps.WebServer.Daemon;

/// <summary>
/// <c>kazowebd</c>, the web server of the hosted static sites (ADR 0077, section 2; ADR 0080). It
/// only drives the OS through the public SDK: it reads the <c>.site</c> files, follows their
/// directories with <see cref="KazoFileSystemWatcher"/>, opens one <see cref="KazoHttpListener"/> per
/// binding and serves files with <see cref="StaticFileHandler"/>.
/// </summary>
/// <remarks>
/// <para>
/// Configuration is applied hot, like <c>nginx -s reload</c> on every change: a new or edited file
/// binds its new addresses, a removed or disabled site closes its own, and an unchanged binding keeps
/// its socket (its requests already in flight finish on the old settings).
/// </para>
/// <para>
/// A site that cannot bind (address already in use, a user site on a port under 1024, an invalid
/// file) does not stop the others: the reason goes to <c>/var/log/kazoweb/error.log</c>.
/// </para>
/// </remarks>
public sealed class KazoWebdService : ServiceAppBase
{
    private readonly Dictionary<string, BindingListener> _listeners = new(StringComparer.Ordinal);
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private Dictionary<string, ServedSite> _sites = new(StringComparer.Ordinal);
    private IAppExecutionContext? _context;

    /// <summary>Initializes the service with its manifest.</summary>
    /// <param name="manifest">The manifest of <c>kazowebd</c>.</param>
    public KazoWebdService(AppManifest manifest)
        : base(manifest)
    {
    }

    /// <summary>Gets the prefixes <c>kazowebd</c> listens on and the site each serves (for tests and diagnostics).</summary>
    public IReadOnlyDictionary<string, string> ServedPrefixes => _sites.ToDictionary(static pair => pair.Key, static pair => pair.Value.Site.Name, StringComparer.Ordinal);

    /// <inheritdoc />
    protected override async Task RunCoreAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken)
    {
        _context = context;
        KazoUserInfo user = KazoUser.Current;
        string userSites = KazoWebSiteFile.GetUserSitesDirectory(user.HomeDirectory);
        await EnsureUserDirectoryAsync(userSites, sessionCancellationToken).ConfigureAwait(false);

        List<KazoFileSystemWatcher> watchers = [];
        try
        {
            foreach (string directory in new[] { KazoWebSiteFile.SystemSitesDirectory, userSites })
            {
                if (await WatchAsync(directory, sessionCancellationToken).ConfigureAwait(false) is { } watcher)
                {
                    watchers.Add(watcher);
                }
            }

            KazoWebdLog.WriteError(context, "notice", null, $"{KazoWebdLog.ServerSignature} starting, user sites in {userSites}");
            await ReloadAsync(sessionCancellationToken).ConfigureAwait(false);
            while (!sessionCancellationToken.IsCancellationRequested)
            {
                await _changes.Reader.ReadAsync(sessionCancellationToken).ConfigureAwait(false);
                await ReloadAsync(sessionCancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (sessionCancellationToken.IsCancellationRequested)
        {
            // Normal stop: logout, systemctl stop, shutdown.
        }
        finally
        {
            foreach (KazoFileSystemWatcher watcher in watchers)
            {
                await watcher.DisposeAsync().ConfigureAwait(false);
            }

            await CloseAllAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override async ValueTask OnStoppingAsync(ServiceStopReason reason, CancellationToken cancellationToken)
    {
        if (_context is { } context)
        {
            KazoWebdLog.WriteError(context, "notice", null, $"exiting ({reason})");
        }

        // Close the sockets now, so a new kazowebd (systemctl restart) can bind them at once.
        await CloseAllAsync().ConfigureAwait(false);
    }

    /// <summary>Reads every declaration again and makes the open sockets match them.</summary>
    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        IAppExecutionContext context = _context!;
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<KazoWebSiteDeclaration> declarations = await KazoWebSiteFile.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            Dictionary<string, ServedSite> desired = new(StringComparer.Ordinal);
            foreach (KazoWebSiteDeclaration declaration in declarations)
            {
                if (!declaration.IsValid)
                {
                    KazoWebdLog.WriteError(context, "error", declaration.Name, $"{declaration.Path}: {string.Join("; ", declaration.Errors)}");
                    continue;
                }

                if (!declaration.Site!.Enabled)
                {
                    continue;
                }

                if (desired.Values.FirstOrDefault(site => string.Equals(site.Site.Name, declaration.Name, StringComparison.Ordinal)) is { } sameName)
                {
                    KazoWebdLog.WriteError(context, "error", declaration.Name, $"{declaration.Path}: a site named '{declaration.Name}' is already declared in {sameName.Declaration.Path}");
                    continue;
                }

                foreach (string value in declaration.Site.Bindings)
                {
                    WebBinding binding = WebAppUrls.Parse(value, allowWildcardHost: true);
                    string prefix = binding.ToString();
                    if (desired.TryGetValue(prefix, out ServedSite? taken))
                    {
                        KazoWebdLog.WriteError(context, "error", declaration.Name, $"binding {prefix} is already used by site '{taken.Site.Name}'");
                        continue;
                    }

                    desired[prefix] = new ServedSite(declaration, binding);
                }
            }

            foreach (string prefix in _listeners.Keys.Where(prefix => !desired.ContainsKey(prefix)).ToList())
            {
                await _listeners[prefix].DisposeAsync().ConfigureAwait(false);
                _listeners.Remove(prefix);
            }

            Dictionary<string, ServedSite> served = new(StringComparer.Ordinal);
            foreach ((string prefix, ServedSite site) in desired)
            {
                if (!_listeners.ContainsKey(prefix))
                {
                    BindingListener? listener = await OpenAsync(prefix, site, cancellationToken).ConfigureAwait(false);
                    if (listener is null)
                    {
                        continue;
                    }

                    _listeners[prefix] = listener;
                    KazoWebdLog.WriteError(context, "notice", site.Site.Name, $"listening on {prefix} (root {site.Site.Root}, as {(site.ServesAsWwwData ? WwwDataAccess.AccountName : site.Declaration.Owner)})");
                }

                served[prefix] = site;
            }

            _sites = served;
            // Accept only once the site table names the new bindings: a request taken earlier would
            // find no site and answer 503, though the port was already open.
            foreach ((string prefix, BindingListener listener) in _listeners)
            {
                listener.Loop ??= AcceptAsync(prefix, listener, listener.Stopping);
            }
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private async Task<BindingListener?> OpenAsync(string prefix, ServedSite site, CancellationToken cancellationToken)
    {
        IAppExecutionContext context = _context!;
        KazoHttpListener listener = new();
        listener.Prefixes.Add(prefix);
        try
        {
            await listener.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (KazoHttpListenerException exception)
        {
            KazoWebdLog.WriteError(context, "emerg", site.Site.Name, $"bind() to {prefix} failed ({exception.ErrorCode}: {exception.Message})");
            await listener.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            KazoWebdLog.WriteError(context, "emerg", site.Site.Name, $"bind() to {prefix} failed (13: Permission denied: {exception.Message})");
            await listener.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Requests wait in the socket's queue until ReloadAsync starts the accept loop.
        return new BindingListener(listener, stop);
    }

    private async Task AcceptAsync(string prefix, BindingListener binding, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            KazoHttpListenerContext request;
            try
            {
                request = await binding.Listener.GetContextAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or KazoHttpListenerException)
            {
                return;
            }

            _ = ServeAsync(prefix, request, cancellationToken);
        }
    }

    private async Task ServeAsync(string prefix, KazoHttpListenerContext request, CancellationToken cancellationToken)
    {
        IAppExecutionContext context = _context!;
        StaticFileResult result;
        string siteName = "-";
        try
        {
            if (!_sites.TryGetValue(prefix, out ServedSite? site))
            {
                // The site was removed while this request waited in the socket's queue.
                request.Response.StatusCode = 503;
                result = new StaticFileResult(503, 0);
            }
            else
            {
                siteName = site.Site.Name;
                result = await StaticFileHandler.HandleAsync(site, request, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            result = new StaticFileResult(500, 0, $"{exception.GetType().Name}: {exception.Message}");
            if (!request.Response.IsClosed)
            {
                request.Response.StatusCode = 500;
            }
        }
        finally
        {
            request.Response.Close();
        }

        if (siteName == "-")
        {
            return;
        }

        try
        {
            KazoWebdLog.WriteAccess(context, siteName, KazoWebdLog.FormatAccess(request.Request, result.StatusCode, result.BodyBytes, context.Clock.UtcNow));
            if (result.Error is { } error)
            {
                KazoWebdLog.WriteError(context, result.StatusCode >= 500 ? "crit" : "error", siteName,
                    $"{error}, client: {request.Request.RemoteEndPoint.Address}, request: \"{request.Request.HttpMethod} {request.Request.RawUrl} HTTP/1.1\", host: \"{request.Request.UserHostName}\"");
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException)
        {
            context.Logging.Log(DiagnosticSeverity.Warning, $"cannot write the logs of site '{siteName}': {exception.Message}");
        }
    }

    private async Task<KazoFileSystemWatcher?> WatchAsync(string directory, CancellationToken cancellationToken)
    {
        KazoFileSystemWatcher watcher = new(directory, "*" + KazoWebSiteFile.Extension);
        watcher.Created += OnChanged;
        watcher.Changed += OnChanged;
        watcher.Deleted += OnChanged;
        watcher.Renamed += OnChanged;
        try
        {
            await watcher.StartAsync(cancellationToken).ConfigureAwait(false);
            return watcher;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Without a watch the sites still load at start; changes then need systemctl restart.
            KazoWebdLog.WriteError(_context!, "warn", null, $"cannot watch {directory}: {exception.Message}");
            await watcher.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    private void OnChanged(object? sender, KazoFileSystemEventArgs e) => _changes.Writer.TryWrite(true);

    private static async Task EnsureUserDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            if (!await KazoDirectory.ExistsAsync(directory, cancellationToken).ConfigureAwait(false))
            {
                await KazoDirectory.CreateDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            KazoExecutionContext.Current.Logging.Log(DiagnosticSeverity.Warning, $"cannot create {directory}: {exception.Message}");
        }
    }

    private async Task CloseAllAsync()
    {
        List<BindingListener> listeners = [.. _listeners.Values];
        _listeners.Clear();
        _sites = new Dictionary<string, ServedSite>(StringComparer.Ordinal);
        foreach (BindingListener listener in listeners)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>One open socket and its accept loop.</summary>
    private sealed class BindingListener(KazoHttpListener listener, CancellationTokenSource stop) : IAsyncDisposable
    {
        public KazoHttpListener Listener { get; } = listener;

        public Task? Loop { get; set; }

        /// <summary>Gets the token that stops the accept loop.</summary>
        public CancellationToken Stopping => stop.Token;

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await Listener.DisposeAsync().ConfigureAwait(false);
            if (Loop is not null)
            {
                await Loop.ConfigureAwait(false);
            }

            stop.Dispose();
        }
    }
}
