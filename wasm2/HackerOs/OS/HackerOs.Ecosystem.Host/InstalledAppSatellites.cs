using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using HackerOs.Platform.Core.Apps.Packages;

namespace HackerOs.Ecosystem;

/// <summary>
/// Loads the satellite assemblies of an installed app (ADR 0222 §2): before its assembly's code
/// runs, the OS reads <c>lib/&lt;culture&gt;/&lt;assembly&gt;.resources.dll</c> from the app's folder for
/// each language HackerOS speaks and loads it into the page's runtime, where the app's
/// <c>ResourceManager</c> (and <c>IStringLocalizer</c>) finds it.
/// </summary>
/// <remarks>
/// <para>
/// A built-in app gets its satellites from Blazor and <c>BrowserSatelliteAssemblies</c>; an installed
/// app's files are in the virtual file system, which the runtime cannot fetch. Every language is
/// loaded, not only the user's: each process has its own <c>LANG</c> (ADR 0104), as the page does
/// for the built-in apps.
/// </para>
/// <para>
/// The satellite is loaded into the default context with <c>LoadFromStream</c>, as the app's own
/// assembly; the <see cref="AssemblyLoadContext.Resolving"/> handler then answers the runtime's
/// by-name request (<c>Kazo.Webamp.resources, Culture=fr</c>) with it, so the lookup works whether
/// or not the runtime matches assemblies loaded from a stream by name.
/// </para>
/// </remarks>
public sealed class InstalledAppSatellites
{
    private static readonly ConcurrentDictionary<(string Name, string Culture), Assembly> s_loaded = new();
    private static int s_resolverInstalled;

    /// <summary>Creates the loader for the given languages.</summary>
    /// <param name="cultures">Culture names, e.g. <c>fr</c>; English, the neutral language, needs no satellite.</param>
    public InstalledAppSatellites(IEnumerable<string> cultures)
    {
        ArgumentNullException.ThrowIfNull(cultures);
        Cultures = [.. cultures
            .Where(static culture => !string.IsNullOrWhiteSpace(culture) && !culture.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Gets the cultures whose satellites are loaded.</summary>
    public IReadOnlyList<string> Cultures { get; }

    /// <summary>
    /// Reads and loads the satellites of one of <paramref name="app"/>'s assemblies. A culture the
    /// package does not translate into is skipped: the app shows its neutral texts.
    /// </summary>
    /// <param name="app">The installed app.</param>
    /// <param name="assemblyFileName">The assembly being loaded, e.g. <c>Kazo.Webamp.dll</c>.</param>
    /// <param name="read">Reads one installed file, <see langword="null"/> when it is missing.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The cultures whose satellite was found, in <see cref="Cultures"/> order.</returns>
    [RequiresUnreferencedCode("Installed assemblies arrive after publishing; AppPackageApiCheck verified their references at install time.")]
    public async Task<IReadOnlyList<string>> LoadAsync(
        InstalledApp app, string assemblyFileName, InstalledAppFileReader read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyFileName);
        ArgumentNullException.ThrowIfNull(read);
        string satelliteName = Path.GetFileNameWithoutExtension(assemblyFileName) + ".resources";
        List<string> found = [];
        foreach (string culture in Cultures)
        {
            if (s_loaded.ContainsKey((satelliteName, culture.ToLowerInvariant())))
            {
                found.Add(culture);
                continue;
            }

            byte[]? bytes = await read(app.GetSatellitePath(culture, assemblyFileName), cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                continue;
            }

            InstallResolver();
            Assembly satellite = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(bytes, writable: false));
            // Keyed by the culture the satellite says it is, which the runtime asks for.
            string loadedCulture = satellite.GetName().CultureName is { Length: > 0 } named ? named : culture;
            s_loaded.TryAdd((satelliteName, loadedCulture.ToLowerInvariant()), satellite);
            found.Add(culture);
        }

        return found;
    }

    /// <summary>Gets a satellite loaded by <see cref="LoadAsync"/>, as the runtime asks for it.</summary>
    /// <param name="name">The requested assembly name, e.g. <c>Kazo.Webamp.resources, Culture=fr</c>.</param>
    /// <returns>The satellite, or <see langword="null"/> when none was loaded under that name.</returns>
    public static Assembly? Find(AssemblyName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Name is not { } simple || string.IsNullOrEmpty(name.CultureName))
        {
            return null;
        }

        // ResourceManager walks the parent cultures itself (fr-CA, then fr): an exact match is enough.
        return s_loaded.GetValueOrDefault((simple, name.CultureName.ToLowerInvariant()));
    }

    private static void InstallResolver()
    {
        if (Interlocked.Exchange(ref s_resolverInstalled, 1) == 0)
        {
            AssemblyLoadContext.Default.Resolving += static (_, name) => Find(name);
        }
    }
}
