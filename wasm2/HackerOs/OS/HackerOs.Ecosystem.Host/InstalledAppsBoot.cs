using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using HackerOs.App.Abstractions;
using HackerOs.Infrastructure.Browser.FileSystem;
using HackerOs.Platform.Blazor.LazyLoading;
using HackerOs.Platform.Core.Apps;
using HackerOs.Platform.Core.Apps.Packages;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HackerOs.Ecosystem;

/// <summary>
/// The catalog a browser host runs with: the apps it was built with, plus the apps installed from
/// packages (ADR 0182 §5, Q5 answer C), read once before the OS starts by
/// <see cref="InstalledAppsBootExtensions.LoadInstalledAppsAsync"/>. A host that never calls it runs
/// with its built-in apps only.
/// </summary>
public sealed class InstalledAppsBoot
{
    private InstalledAppFileReader? _read;

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

    /// <summary>Gets the lines to write to <c>/var/log/syslog</c> about installed apps left out.</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    /// <summary>Gets whether the installed apps were read (the catalog can no longer change).</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Joins the installed apps to the catalog. Called once, before the catalog is used.</summary>
    /// <param name="image">The installed apps.</param>
    /// <param name="read">Reads their files afterwards (assemblies and assets).</param>
    public void Apply(InstalledAppsImage image, InstalledAppFileReader read)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(read);
        if (IsLoaded)
        {
            throw new InvalidOperationException("The installed apps were already read for this session.");
        }

        Current = BuiltIn.WithInstalledApps(image, out IReadOnlyList<string> warnings);
        Warnings = [.. image.Warnings, .. warnings];
        _read = read;
        IsLoaded = true;
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
/// Loads an installed app's assemblies from <c>/usr/lib/hackeros/apps</c> (ADR 0182 §5) and every
/// other declared assembly through the host's lazy loader. Before an installed app's code loads, its
/// <c>wwwroot/</c> files are published at <c>_content/&lt;assembly&gt;/</c> (ADR 0206).
/// </summary>
/// <param name="inner">The host's own transport (Blazor's lazy loader).</param>
/// <param name="installed">The installed apps of this session.</param>
/// <param name="assets">Serves their <c>wwwroot/</c> files; <see langword="null"/> serves none.</param>
public sealed class InstalledAppAssemblyTransport(IBuildKnownAssemblyTransport inner, InstalledAppsBoot installed, InstalledAppAssetPublisher? assets = null) : IBuildKnownAssemblyTransport
{
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
    /// Reads <c>[installed]</c> of <c>/etc/hackeros/apps.config</c> and each app's manifest straight
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
        InstalledAppFileReader read = InstalledAppsLayout.CreateReader(scope.ServiceProvider.GetRequiredService<IndexedDbFileSystemProvider>());
        InstalledAppsImage image;
        try
        {
            image = await InstalledAppsImage.LoadAsync(read).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            host.Services.GetService<ILoggerFactory>()?.CreateLogger(typeof(InstalledAppsBoot))
                .LogWarning(exception, "apps: installed apps could not be read, starting with the built-in apps only");
            image = InstalledAppsImage.Empty;
        }

        boot.Apply(image, read);
        ILogger? logger = host.Services.GetService<ILoggerFactory>()?.CreateLogger(typeof(InstalledAppsBoot));
        foreach (string warning in boot.Warnings)
        {
            logger?.LogWarning("{Warning}", warning);
        }
    }
}
