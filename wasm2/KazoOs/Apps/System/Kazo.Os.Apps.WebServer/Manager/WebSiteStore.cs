using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.AppSdk.Net.Hosting;
using Kazo.Os.AppSdk.ServiceProcess;
using Kazo.Os.Apps.WebServer.Daemon;

namespace Kazo.Os.Apps.WebServer.Manager;

/// <summary>What Web Server Manager shows for a site.</summary>
public enum WebSiteState
{
    /// <summary><c>hackwebd</c> listens on every binding of the site.</summary>
    Running,

    /// <summary><c>hackwebd</c> listens on some bindings only (the others failed: see the error log).</summary>
    Partial,

    /// <summary>The site is valid and enabled but nothing listens: <c>hackwebd</c> is stopped, or every bind failed.</summary>
    Down,

    /// <summary>The site is disabled (<c>"enabled": false</c>).</summary>
    Stopped,

    /// <summary>The <c>.site</c> file is invalid; <c>hackwebd</c> ignores it.</summary>
    Invalid,

    /// <summary>A web app (ADR 0073) served by its own process: read-only here, started on demand.</summary>
    External
}

/// <summary>One row of Web Server Manager.</summary>
/// <param name="Name">Site name, or the app name of an external site.</param>
/// <param name="Declaration">The <c>.site</c> file, or <see langword="null"/> for an external site.</param>
/// <param name="App">The web app of an external site.</param>
/// <param name="Urls">Its addresses, main one first.</param>
/// <param name="State">Its state.</param>
public sealed record WebSiteRow(string Name, KazoWebSiteDeclaration? Declaration, KazoAppInfo? App, IReadOnlyList<string> Urls, WebSiteState State)
{
    /// <summary>Gets whether the row is a <c>.site</c> file the user may edit (with approval for a system site).</summary>
    public bool IsEditable => Declaration is not null;

    /// <summary>Gets the main URL to open in the browser, or <see langword="null"/>.</summary>
    public string? MainUrl => Urls.FirstOrDefault(static url => !url.Contains("://+", StringComparison.Ordinal) && !url.Contains("://*", StringComparison.Ordinal))
        ?? Urls.FirstOrDefault()?.Replace("://+", "://localhost", StringComparison.Ordinal).Replace("://*", "://localhost", StringComparison.Ordinal);
}

/// <summary>Everything Web Server Manager shows at one moment.</summary>
/// <param name="Daemon">The <c>hackwebd</c> service, or <see langword="null"/> when it is not installed.</param>
/// <param name="Rows">System sites, then the user's, then external sites.</param>
/// <param name="Listeners">Every listening socket of the machine.</param>
public sealed record WebServerSnapshot(KazoServiceController? Daemon, IReadOnlyList<WebSiteRow> Rows, IReadOnlyList<KazoTcpListenerInfo> Listeners);

/// <summary>
/// The logic of Web Server Manager, without UI (ADR 0077, section 3): it only drives the OS through the
/// public SDK, like IIS Manager editing <c>applicationHost.config</c>. Sites are <c>.site</c> files;
/// their live state is read from the listening sockets of <c>hackwebd</c>
/// (<see cref="KazoIPGlobalProperties"/>, what <c>netstat -tlnp</c> prints).
/// </summary>
public static class WebSiteStore
{
    /// <summary>First port proposed for a new site.</summary>
    public const int FirstSuggestedPort = 8080;

    /// <summary>Reads the declarations, the external sites, the service and the sockets.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<WebServerSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<KazoWebSiteDeclaration> declarations = await KazoWebSiteFile.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        KazoServiceController[] services = await KazoServiceController.GetServicesAsync(cancellationToken).ConfigureAwait(false);
        KazoServiceController? daemon = services.FirstOrDefault(static service => service.ServiceName == KazoWebdApp.AppId);
        IReadOnlyList<KazoTcpListenerInfo> listeners = await KazoIPGlobalProperties.GetActiveTcpListenersAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<KazoAppInfo> apps = await KazoApps.GetInstalledAsync(cancellationToken).ConfigureAwait(false);

        HashSet<string> served = new(
            listeners
                .Where(listener => daemon?.ProcessId is { } pid && listener.ProcessId == pid)
                .SelectMany(static listener => listener.Prefixes),
            StringComparer.Ordinal);

        List<WebSiteRow> rows = [];
        foreach (KazoWebSiteDeclaration declaration in declarations)
        {
            IReadOnlyList<string> urls = declaration.Site?.Bindings ?? [];
            rows.Add(new WebSiteRow(declaration.Name, declaration, null, urls, StateOf(declaration, served)));
        }

        foreach (KazoAppInfo app in apps.Where(static app => app.Kind == AppKind.Web).OrderBy(static app => app.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new WebSiteRow(app.Name, null, app, app.Urls, WebSiteState.External));
        }

        return new WebServerSnapshot(daemon, rows, listeners);
    }

    /// <summary>Computes the state of a declared site from the prefixes <c>hackwebd</c> listens on.</summary>
    /// <param name="declaration">The site file.</param>
    /// <param name="servedPrefixes">Normalized prefixes of the sockets owned by <c>hackwebd</c>.</param>
    public static WebSiteState StateOf(KazoWebSiteDeclaration declaration, IReadOnlySet<string> servedPrefixes)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        if (!declaration.IsValid)
        {
            return WebSiteState.Invalid;
        }

        if (!declaration.Site!.Enabled)
        {
            return WebSiteState.Stopped;
        }

        int listening = declaration.Site.Bindings.Count(binding =>
            KazoWebSiteFile.TryParseBinding(binding, out WebBinding parsed, out _) && servedPrefixes.Contains(parsed.ToString()));
        return listening == declaration.Site.Bindings.Count ? WebSiteState.Running
            : listening > 0 ? WebSiteState.Partial
            : WebSiteState.Down;
    }

    /// <summary>
    /// Proposes a site for a folder, as "Publish this folder as a site" does: the folder name as site
    /// name, the first free port from <see cref="FirstSuggestedPort"/> on <c>localhost</c>, a user site.
    /// </summary>
    /// <param name="folder">Absolute path of the folder.</param>
    /// <param name="snapshot">The current state (to avoid taken names and ports).</param>
    public static KazoWebSite Suggest(string folder, WebServerSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(snapshot);
        string baseName = new([.. (KazoPath.GetFileName(folder.TrimEnd('/')) ?? "site")
            .ToLowerInvariant()
            .Select(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-')]);
        baseName = baseName.Trim('-', '.', '_');
        if (!KazoWebSiteFile.IsValidName(baseName))
        {
            baseName = "site";
        }

        HashSet<string> names = new(snapshot.Rows.Select(static row => row.Name), StringComparer.OrdinalIgnoreCase);
        string name = baseName;
        for (int index = 2; names.Contains(name); index++)
        {
            name = $"{baseName}-{index}";
        }

        HashSet<int> ports = [.. snapshot.Listeners.Select(static listener => listener.Port)];
        foreach (WebSiteRow row in snapshot.Rows)
        {
            foreach (string url in row.Urls)
            {
                if (WebAppUrls.TryParse(url, allowWildcardHost: true, out WebBinding binding, out _))
                {
                    ports.Add(binding.Port);
                }
            }
        }

        int port = FirstSuggestedPort;
        while (ports.Contains(port))
        {
            port++;
        }

        return new KazoWebSite
        {
            Name = name,
            Root = folder.TrimEnd('/') is { Length: > 0 } root ? root : "/",
            Bindings = [$"http://localhost:{port}/"],
            DefaultDocuments = KazoWebSite.DefaultDefaultDocuments,
            DirectoryBrowsing = false,
            Enabled = true
        };
    }

    /// <summary>Gets the directory of the sites of <paramref name="scope"/> for the current user.</summary>
    /// <param name="scope">The scope.</param>
    public static string DirectoryOf(KazoWebSiteScope scope) => scope == KazoWebSiteScope.System
        ? KazoWebSiteFile.SystemSitesDirectory
        : KazoWebSiteFile.GetUserSitesDirectory(KazoUser.Current.HomeDirectory);

    /// <summary>
    /// Writes a site file. Renaming a site writes the new file, then deletes the old one. Writing in
    /// <c>/etc/hackweb/sites</c> needs an administrator: the write is refused with
    /// <see cref="UnauthorizedAccessException"/> until one approves it (the window offers the prompt).
    /// </summary>
    /// <param name="site">The site.</param>
    /// <param name="scope">Where to declare it.</param>
    /// <param name="previousPath">The file it was read from, when editing.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>The path written.</returns>
    /// <exception cref="ArgumentException">The site is invalid for <paramref name="scope"/>.</exception>
    public static async Task<string> SaveAsync(KazoWebSite site, KazoWebSiteScope scope, string? previousPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(site);
        IReadOnlyList<string> errors = KazoWebSiteFile.Validate(site, scope);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join("; ", errors), nameof(site));
        }

        string directory = DirectoryOf(scope);
        if (!await KazoDirectory.ExistsAsync(directory, cancellationToken).ConfigureAwait(false))
        {
            await KazoDirectory.CreateDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
        }

        string path = KazoWebSiteFile.GetPath(directory, site.Name);
        await KazoFile.WriteAllTextAsync(path, KazoWebSiteFile.Serialize(site), cancellationToken).ConfigureAwait(false);
        if (previousPath is not null && !string.Equals(previousPath, path, StringComparison.Ordinal)
            && await KazoFile.ExistsAsync(previousPath, cancellationToken).ConfigureAwait(false))
        {
            await KazoFile.DeleteAsync(previousPath, cancellationToken).ConfigureAwait(false);
        }

        return path;
    }

    /// <summary>Deletes a site file; <c>hackwebd</c> closes its sockets at once.</summary>
    /// <param name="declaration">The site.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    public static Task DeleteAsync(KazoWebSiteDeclaration declaration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        return KazoFile.DeleteAsync(declaration.Path, cancellationToken);
    }

    /// <summary>
    /// Explains why <c>www-data</c> cannot serve <paramref name="root"/>, for a system site, or returns
    /// <see langword="null"/> when it can.
    /// </summary>
    /// <param name="root">The site root.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<string?> CheckWwwDataAsync(string root, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !root.StartsWith('/'))
        {
            return null;
        }

        if (!await KazoDirectory.ExistsAsync(root, cancellationToken).ConfigureAwait(false))
        {
            return Strings.Ambient["Warning.RootMissing", root];
        }

        return await WwwDataAccess.CanTraverseAsync(root, cancellationToken).ConfigureAwait(false)
            ? null
            : Strings.Ambient["Warning.WwwDataCannotEnter", WwwDataAccess.AccountName, root].Value;
    }

    /// <summary>Reads the last lines of the access log of a site and its lines of the error log.</summary>
    /// <param name="site">The site name.</param>
    /// <param name="maxLines">Lines kept from each log.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The access lines and the error lines, oldest first.</returns>
    public static async Task<(IReadOnlyList<string> Access, IReadOnlyList<string> Errors)> ReadLogsAsync(
        string site, int maxLines = 100, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> access = await TailAsync(KazoWebdLog.AccessLogPath(site), static _ => true, maxLines, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> errors = await TailAsync(
            KazoWebdLog.ErrorLogPath,
            line => line.Contains($": {site}: ", StringComparison.Ordinal),
            maxLines,
            cancellationToken).ConfigureAwait(false);
        return (access, errors);
    }

    private static async Task<IReadOnlyList<string>> TailAsync(string path, Func<string, bool> filter, int maxLines, CancellationToken cancellationToken)
    {
        try
        {
            if (!await KazoFile.ExistsAsync(path, cancellationToken).ConfigureAwait(false))
            {
                return [];
            }

            string[] lines = await KazoFile.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
            return [.. lines.Where(static line => line.Length > 0).Where(filter).TakeLast(maxLines)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [Strings.Ambient["Logs.ReadFailed", path, exception.Message]];
        }
    }
}
