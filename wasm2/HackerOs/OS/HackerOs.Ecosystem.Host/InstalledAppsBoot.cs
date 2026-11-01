using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using HackerOs.App.Abstractions;
using HackerOs.Infrastructure.Browser.FileSystem;
using HackerOs.Platform.Blazor.LazyLoading;
using HackerOs.Platform.Core;
using HackerOs.Platform.Core.Apps;
using HackerOs.Platform.Core.Apps.Packages;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HackerOs.Ecosystem;

/// <summary>
/// The catalog a browser host runs with: the apps it was built with, plus the apps installed from
/// packages (ADR 0182 §5), read before the OS starts by
/// <see cref="InstalledAppsBootExtensions.LoadInstalledAppsAsync"/>, then again after each install,
/// update or removal (<see cref="Refresh"/>, ADR 0214 §1). A host that never calls it runs with its
/// built-in apps only.
/// </summary>
public sealed class InstalledAppsBoot
{
    private readonly Lock _gate = new();
    private InstalledAppFileReader? _read;
    private InstalledAppHomeLister? _listHomes;
    private AppCatalog? _live;

    /// <summary>Creates the holder over the host's own catalog.</summary>
    /// <param name="builtIn">The apps the host was built with.</param>
    public InstalledAppsBoot(HostAppCatalog builtIn)
    {
        BuiltIn = builtIn ?? throw new ArgumentNullException(nameof(builtIn));
        Current = builtIn;
    }

    /// <summary>Gets the apps the host was built with.</summary>
    public HostAppCatalog BuiltIn { get; }

    /// <summary>Gets the catalog the OS runs with.</summary>
    public HostAppCatalog Current { get; private set; }

    /// <summary>
    /// Gets the one <see cref="AppCatalog"/> instance every service of the page holds (ADR 0214 §1): it
    /// takes the contents of <see cref="Current"/> each time the installed apps change. It is never the
    /// built-in catalog itself, which must keep only the apps the host was built with.
    /// </summary>
    public AppCatalog Live
    {
        get
        {
            lock (_gate)
            {
                return _live ??= Current.Catalog.CreateLiveCopy();
            }
        }
    }

    /// <summary>Gets the lines to write to <c>/var/log/syslog</c> about installed apps left out.</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    /// <summary>
    /// Gets the installed apps the catalog accepted, with who sees each one (ADR 0189 §3): filled by
    /// <see cref="Apply"/>, shared with every session of the page.
    /// </summary>
    public InstalledPackageCatalog Packages { get; } = new();

    /// <summary>Gets whether the installed apps were read at boot.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Joins the installed apps to the catalog. Called once, at boot, before the catalog is used.</summary>
    /// <param name="image">The installed apps.</param>
    /// <param name="read">Reads their files afterwards (assemblies and assets).</param>
    /// <param name="listHomes">Lists the users' homes again when the installed apps change (ADR 0214 §1).</param>
    public void Apply(InstalledAppsImage image, InstalledAppFileReader read, InstalledAppHomeLister? listHomes = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(read);
        if (IsLoaded)
        {
            throw new InvalidOperationException("The installed apps were already read for this session.");
        }

        Current = BuiltIn.WithInstalledApps(image, out IReadOnlyList<string> warnings);
        Warnings = [.. image.Warnings, .. warnings];
        Packages.Apply(Current.Installed);
        _read = read;
        _listHomes = listHomes;
        IsLoaded = true;
        lock (_gate)
        {
            // A service that already holds the catalog (resolved before the boot read) sees the installed apps too.
            _live?.ReplaceContents(Current.Catalog);
        }
    }

    /// <summary>
    /// Reads the installed apps again with the reader given at boot, then <see cref="Refresh"/>es the
    /// running catalog with them (ADR 0214 §1).
    /// </summary>
    /// <param name="isLoaded">Whether an installed app's code is already loaded in the page.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>What changed; <see cref="InstalledAppsActivation.None"/> before the boot read.</returns>
    public async ValueTask<InstalledAppsActivation> ReloadAsync(Func<InstalledApp, bool> isLoaded, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isLoaded);
        if (_read is not { } read)
        {
            return InstalledAppsActivation.None;
        }

        InstalledAppsImage image = await InstalledAppsImage.LoadAsync(read, _listHomes, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Refresh(image, isLoaded);
    }

    /// <summary>
    /// Replaces the installed apps of the running catalog with <paramref name="image"/> (ADR 0214 §1). An
    /// app whose code is loaded (<paramref name="isLoaded"/>) keeps the version it runs: a .NET assembly
    /// cannot leave the page, so its update or removal waits for the next start of the OS (Q1 answer A).
    /// </summary>
    /// <param name="image">The installed apps as they are now on disk.</param>
    /// <param name="isLoaded">Whether an installed app's code is already loaded in the page.</param>
    /// <returns>What changed, and what waits for a restart.</returns>
    public InstalledAppsActivation Refresh(InstalledAppsImage image, Func<InstalledApp, bool> isLoaded)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(isLoaded);
        lock (_gate)
        {
            Dictionary<string, InstalledApp> running = Current.Installed.ToDictionary(static app => app.AppId, StringComparer.Ordinal);
            Dictionary<string, InstalledApp> wanted = image.Apps.ToDictionary(static app => app.AppId, StringComparer.Ordinal);
            List<InstalledApp> merged = [];
            List<string> waiting = [];
            foreach (string appId in running.Keys.Union(wanted.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                InstalledApp? before = running.GetValueOrDefault(appId);
                InstalledApp? after = wanted.GetValueOrDefault(appId);
                bool changes = after is null || before is null || after.Version != before.Version || after.FilesHome != before.FilesHome;
                if (before is not null && changes && isLoaded(before))
                {
                    // The loaded copy stays; who may see it follows the files on disk.
                    merged.Add(before with { OwnerHomes = after?.OwnerHomes ?? before.OwnerHomes });
                    waiting.Add(appId);
                }
                else if (after is not null)
                {
                    merged.Add(after);
                }
            }

            HostAppCatalog next = BuiltIn.WithInstalledApps(new InstalledAppsImage(merged, image.Warnings), out IReadOnlyList<string> warnings);
            Dictionary<string, InstalledApp> accepted = next.Installed.ToDictionary(static app => app.AppId, StringComparer.Ordinal);
            InstalledAppsActivation activation = new(
                [.. accepted.Keys.Where(appId => !running.ContainsKey(appId)).Order(StringComparer.Ordinal)],
                [.. accepted.Values.Where(app => running.TryGetValue(app.AppId, out InstalledApp? old) && old.Version != app.Version).Select(static app => app.AppId).Order(StringComparer.Ordinal)],
                [.. running.Keys.Where(appId => !accepted.ContainsKey(appId)).Order(StringComparer.Ordinal)],
                waiting);
            Current = next;
            Warnings = [.. image.Warnings, .. warnings];
            // Who sees each app first: the catalog's listeners hide other users' personal apps right away.
            Packages.Apply(next.Installed);
            (_live ??= next.Catalog.CreateLiveCopy()).ReplaceContents(next.Catalog);
            return activation;
        }
    }

    /// <summary>Finds the installed app that declares an assembly file name.</summary>
    public InstalledApp? FindByAssembly(string assemblyName) =>
        Current.Installed.FirstOrDefault(app => app.AssemblyNames.Contains(assemblyName));

    /// <summary>Finds an installed app by ID.</summary>
    public InstalledApp? Find(string appId) =>
        Current.Installed.FirstOrDefault(app => string.Equals(app.AppId, appId, StringComparison.Ordinal));

    /// <summary>Reads one installed file.</summary>
    internal ValueTask<byte[]?> ReadAsync(VirtualPath path, CancellationToken cancellationToken) =>
        _read is null ? ValueTask.FromResult<byte[]?>(null) : _read(path, cancellationToken);
}

/// <summary>
/// Adds, updates and removes installed apps in the running catalog after each change (ADR 0214 §1):
/// reads them again, keeps an app whose code is loaded in the page as it is (Q1 answer A), and lets
/// the lazy loader fetch the new apps' assemblies.
/// </summary>
/// <param name="installed">The installed apps of this page.</param>
/// <param name="assemblies">The lazy loader, which refuses any assembly it was not told about.</param>
/// <param name="logger">Writes what changed to <c>/var/log/syslog</c>.</param>
public sealed class InstalledAppsActivator(
    InstalledAppsBoot installed,
    BuildKnownAssemblyLoaderRegistry assemblies,
    ILogger<InstalledAppsActivator>? logger = null) : IInstalledAppsActivator
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc />
    public async ValueTask<InstalledAppsActivation> ActivateAsync(CancellationToken cancellationToken = default)
    {
        // One reload at a time: two installs finishing together must not swap the catalog out of order.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            InstalledAppsActivation activation = await installed.ReloadAsync(IsLoaded, cancellationToken).ConfigureAwait(false);
            assemblies.AddKnownAssemblies(installed.Current.Installed.SelectMany(static app => app.AssemblyNames));
            foreach (string warning in installed.Warnings)
            {
                logger?.LogWarning("{Warning}", warning);
            }

            Log("added", activation.Added);
            Log("updated", activation.Updated);
            Log("removed", activation.Removed);
            if (activation.WaitingForRestart.Count > 0)
            {
                logger?.LogInformation(
                    "apps: {AppIds} already loaded, the change applies at the next start of the OS",
                    string.Join(", ", activation.WaitingForRestart));
            }

            return activation;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Gets whether any assembly of an installed app is already in the page (it cannot be unloaded).</summary>
    public static bool IsLoaded(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        IReadOnlySet<string> names = app.AssemblyNames;
        return AssemblyLoadContext.Default.Assemblies.Any(assembly => names.Contains(assembly.GetName().Name + ".dll"));
    }

    private void Log(string what, IReadOnlyList<string> appIds)
    {
        if (appIds.Count > 0)
        {
            logger?.LogInformation("apps: {AppIds} {What} in the running catalog, no restart needed", string.Join(", ", appIds), what);
        }
    }
}

/// <summary>
/// Loads an installed app's assemblies from <c>/usr/lib/hackeros/apps</c> (ADR 0182 §5) and every
/// other declared assembly through the host's lazy loader. Before an installed app's code loads, its
/// <c>wwwroot/</c> files are published at <c>_content/&lt;assembly&gt;/</c> (ADR 0206) and its
/// satellite assemblies are loaded (ADR 0222 §2).
/// </summary>
/// <param name="inner">The host's own transport (Blazor's lazy loader).</param>
/// <param name="installed">The installed apps of this session.</param>
/// <param name="assets">Serves their <c>wwwroot/</c> files; <see langword="null"/> serves none.</param>
public sealed class InstalledAppAssemblyTransport(IBuildKnownAssemblyTransport inner, InstalledAppsBoot installed, InstalledAppAssetPublisher? assets = null) : IBuildKnownAssemblyTransport
{
    /// <summary>Gets the loader of their satellite assemblies (ADR 0222 §2); <see langword="null"/> loads none.</summary>
    public InstalledAppSatellites? Satellites { get; init; }

    /// <inheritdoc />
    [RequiresUnreferencedCode("Installed assemblies arrive after publishing; AppPackageApiCheck verified their references at install time.")]
    public async Task<IReadOnlyList<Assembly>> LoadAsync(IReadOnlyList<string> assemblyNames, CancellationToken cancellationToken)
    {
        List<Assembly> loaded = [];
        List<string> builtIn = [];
        foreach (string name in assemblyNames)
        {
            InstalledApp? app = installed.FindByAssembly(name);
            if (app is null)
            {
                builtIn.Add(name);
                continue;
            }

            if (assets is not null)
            {
                await assets.PublishAsync(app, cancellationToken).ConfigureAwait(false);
            }

            Assembly? existing = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name + ".dll", name, StringComparison.Ordinal));
            if (existing is not null)
            {
                loaded.Add(existing);
                continue;
            }

            byte[] bytes = await installed.ReadAsync(app.GetAssemblyPath(name), cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException($"{app.GetAssemblyPath(name).Value} is missing.", name);
            if (Satellites is { } satellites)
            {
                // ADR 0222 §2: the translations are in the runtime before the app's first render.
                await satellites.LoadAsync(app, name, installed.ReadAsync, cancellationToken).ConfigureAwait(false);
            }

            loaded.Add(AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(bytes, writable: false)));
        }

        if (builtIn.Count > 0)
        {
            loaded.AddRange(await inner.LoadAsync(builtIn, cancellationToken).ConfigureAwait(false));
        }

        return loaded;
    }
}

/// <summary>Reads an installed app's assets from its folder, every other app's from the host (ADR 0182 §5).</summary>
/// <param name="inner">The host's own asset source.</param>
/// <param name="installed">The installed apps of this session.</param>
public sealed class InstalledAppAssetSource(IAppAssetSource inner, InstalledAppsBoot installed) : IAppAssetSource
{
    /// <inheritdoc />
    public ValueTask<byte[]?> ReadAsync(AppManifest manifest, string assetPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return installed.Find(manifest.Id) is { } app && AppPackage.IsSafeRelativePath(assetPath)
            ? installed.ReadAsync(app.GetAssetPath(assetPath), cancellationToken)
            : inner.ReadAsync(manifest, assetPath, cancellationToken);
    }
}

/// <summary>Reads the installed apps before a browser host starts (ADR 0182 §5).</summary>
public static class InstalledAppsBootExtensions
{
    /// <summary>
    /// Reads <c>[installed]</c> of <c>/etc/hackeros/apps.config</c>, of every user's
    /// <c>~/.config/hackeros/installed-apps.config</c> (ADR 0189 §3) and each app's manifest straight
    /// from IndexedDB, then joins those apps to the catalog. Call between <c>Build()</c> and
    /// <c>RunAsync()</c>; any failure leaves the built-in apps only.
    /// </summary>
    /// <param name="host">The built host.</param>
    /// <returns>A task that completes when the catalog is final.</returns>
    public static async Task LoadInstalledAppsAsync(this WebAssemblyHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        InstalledAppsBoot? boot = host.Services.GetService<InstalledAppsBoot>();
        if (boot is null || boot.IsLoaded)
        {
            return;
        }

        // The scope lives as long as the page: its provider serves the installed files afterwards.
        IServiceScope scope = host.Services.CreateScope();
        IndexedDbFileSystemProvider provider = scope.ServiceProvider.GetRequiredService<IndexedDbFileSystemProvider>();
        InstalledAppFileReader read = InstalledAppsLayout.CreateReader(provider);
        InstalledAppsImage image;
        try
        {
            image = await InstalledAppsImage.LoadAsync(read, InstalledAppsLayout.CreateHomeLister(provider)).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            host.Services.GetService<ILoggerFactory>()?.CreateLogger(typeof(InstalledAppsBoot))
                .LogWarning(exception, "apps: installed apps could not be read, starting with the built-in apps only");
            image = InstalledAppsImage.Empty;
        }

        boot.Apply(image, read, InstalledAppsLayout.CreateHomeLister(provider));
        ILogger? logger = host.Services.GetService<ILoggerFactory>()?.CreateLogger(typeof(InstalledAppsBoot));
        foreach (string warning in boot.Warnings)
        {
            logger?.LogWarning("{Warning}", warning);
        }
    }
}
