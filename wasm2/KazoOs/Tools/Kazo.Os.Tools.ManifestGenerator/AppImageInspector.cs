using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Tools.ManifestGenerator;

/// <summary>
/// Measures the executable image of a built-in app for the host's <c>BuiltInApplications.json</c>
/// (ADR 0068, image layer): the size of the app's own assembly, and of every assembly it references,
/// directly or not, that ships with it.
/// </summary>
/// <remarks>
/// <para>
/// References are read from the assembly metadata (no code is loaded) and resolved by name against
/// the app project's dependency list, the file the <c>GenerateHackerOsAppManifests</c> target writes
/// (copy-local assemblies first, then compile references).
/// </para>
/// <para>
/// The .NET runtime and framework (<c>System.*</c>, <c>Microsoft.*</c>, <c>netstandard</c>,
/// <c>mscorlib</c>) are not counted: they are loaded once for the OS itself, like the kernel and the
/// C library every process of a real system shares. Reference assemblies (a <c>ref</c> directory)
/// are skipped: they hold no code and their size means nothing.
/// </para>
/// </remarks>
public static class AppImageInspector
{
    private static readonly string[] FrameworkPrefixes = ["System.", "Microsoft."];
    private static readonly string[] FrameworkNames = ["System", "netstandard", "mscorlib", "WindowsBase"];

    /// <summary>Measures the image of the app built into <paramref name="assemblyPath"/>.</summary>
    /// <param name="assemblyPath">The app's built assembly.</param>
    /// <param name="dependencyPaths">The app project's dependency list, in priority order.</param>
    /// <returns>The image: the app's assembly (not shared) and its shipped dependencies (shared).</returns>
    public static AppImage Inspect(string assemblyPath, IEnumerable<string> dependencyPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentNullException.ThrowIfNull(dependencyPaths);

        Dictionary<string, string> candidates = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in dependencyPaths)
        {
            if (string.IsNullOrWhiteSpace(path)
                || !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || IsReferenceAssemblyPath(path)
                || !File.Exists(path))
            {
                continue;
            }

            candidates.TryAdd(Path.GetFileNameWithoutExtension(path), path);
        }

        string ownName = Path.GetFileNameWithoutExtension(assemblyPath);
        List<AppImageAssembly> assemblies = [new(Path.GetFileName(assemblyPath), new FileInfo(assemblyPath).Length, Shared: false)];
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase) { ownName };
        Queue<string> pending = new([assemblyPath]);
        while (pending.TryDequeue(out string? current))
        {
            foreach (string reference in ReadReferences(current))
            {
                if (IsFramework(reference) || !visited.Add(reference) || !candidates.TryGetValue(reference, out string? path))
                {
                    continue;
                }

                assemblies.Add(new AppImageAssembly(Path.GetFileName(path), new FileInfo(path).Length, Shared: true));
                pending.Enqueue(path);
            }
        }

        return new AppImage([.. assemblies.OrderBy(static assembly => assembly.Name, StringComparer.Ordinal)]);
    }

    /// <summary>Gets whether an assembly name belongs to the .NET runtime or framework.</summary>
    /// <param name="name">The simple assembly name.</param>
    public static bool IsFramework(string name) =>
        FrameworkNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        || FrameworkPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool IsReferenceAssemblyPath(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        return directory is not null
            && (string.Equals(Path.GetFileName(directory), "ref", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(directory), "refint", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ReadReferences(string path)
    {
        List<string> names = [];
        try
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader reader = new(stream);
            if (!reader.HasMetadata)
            {
                return names;
            }

            MetadataReader metadata = reader.GetMetadataReader();
            foreach (AssemblyReferenceHandle handle in metadata.AssemblyReferences)
            {
                names.Add(metadata.GetString(metadata.GetAssemblyReference(handle).Name));
            }
        }
        catch (BadImageFormatException)
        {
            // A native or corrupt file maps no further assembly.
        }

        return names;
    }
}
