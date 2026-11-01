using HackerOs.Platform.Core.Apps.Packages;

namespace HackerOs.Tools.ManifestGenerator;

/// <summary>
/// The satellite assemblies <c>pack</c> puts in a package (ADR 0222 §1): those the build wrote next
/// to the app's own assemblies, <c>&lt;bin&gt;/&lt;culture&gt;/&lt;assembly&gt;.resources.dll</c>, for the
/// assemblies the manifest declares only. The satellites of the SDK and of other libraries stay out:
/// the OS brings its own.
/// </summary>
internal static class PackagedSatellites
{
    /// <summary>Finds the satellites of <paramref name="declaredAssemblies"/> in a build output folder.</summary>
    /// <param name="bin">The build output folder (<c>bin/&lt;configuration&gt;/&lt;tfm&gt;</c>).</param>
    /// <param name="declaredAssemblies">File names the manifest declares, e.g. <c>Kazo.Webamp.dll</c>.</param>
    /// <returns>Each satellite's bytes by its path under <c>lib/</c> (<c>fr/Kazo.Webamp.resources.dll</c>), sorted.</returns>
    public static SortedDictionary<string, byte[]> Find(string bin, IReadOnlySet<string> declaredAssemblies)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bin);
        ArgumentNullException.ThrowIfNull(declaredAssemblies);
        SortedDictionary<string, byte[]> satellites = new(StringComparer.Ordinal);
        if (!Directory.Exists(bin))
        {
            return satellites;
        }

        foreach (string cultureDirectory in Directory.EnumerateDirectories(bin))
        {
            string culture = Path.GetFileName(cultureDirectory);
            foreach (string assembly in declaredAssemblies)
            {
                string fileName = Path.GetFileNameWithoutExtension(assembly) + AppPackage.SatelliteSuffix;
                string relative = $"{culture}/{fileName}";
                string source = Path.Combine(cultureDirectory, fileName);
                // The same check the OS makes: a folder that is no culture name (runtimes/, wwwroot/) is skipped.
                if (File.Exists(source) && AppPackage.TryParseSatellitePath(AppPackage.LibraryFolder + relative, out _, out _))
                {
                    satellites[relative] = File.ReadAllBytes(source);
                }
            }
        }

        return satellites;
    }
}
