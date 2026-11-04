using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace HackerOs.Tools.ManifestGenerator;

/// <summary>
/// Isolated, collectible load context for one compiled app assembly (ADR 0047 §2). Dependencies are
/// resolved by simple name: assemblies the tool itself runs on (the shared .NET and ASP.NET Core
/// frameworks, <c>HackerOs.App.Abstractions</c> so that <c>AppDefinition</c> keeps one type identity)
/// come from the default context; everything else comes from the explicit list MSBuild passes
/// (<c>@(ReferenceCopyLocalPaths)</c> then <c>@(ReferencePath)</c>), skipping reference assemblies,
/// which cannot be loaded for execution.
/// </summary>
internal sealed class AppAssemblyLoadContext : AssemblyLoadContext
{
    private readonly Dictionary<string, string> _candidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _defaultContextNames;

    /// <summary>Creates a context resolving from <paramref name="candidatePaths"/>, first match per name wins.</summary>
    /// <param name="candidatePaths">Dependency paths in priority order.</param>
    public AppAssemblyLoadContext(IEnumerable<string> candidatePaths)
        : base("HackerOs.ManifestGenerator.App", isCollectible: true)
    {
        foreach (string path in candidatePaths)
        {
            if (!File.Exists(path) || !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string name = Path.GetFileNameWithoutExtension(path);
            if (!_candidates.ContainsKey(name) && !IsReferenceAssembly(path))
            {
                _candidates[name] = path;
            }
        }

        // TRUSTED_PLATFORM_ASSEMBLIES lists every assembly the default context can bind: the shared
        // frameworks plus this tool's own dependencies.
        string tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        _defaultContextNames = new HashSet<string>(
            tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(Path.GetFileNameWithoutExtension)!,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string? name = assemblyName.Name;
        if (name is null || _defaultContextNames.Contains(name))
        {
            // null defers to the default context: one shared copy of the framework and of the
            // KazoOS contracts the tool itself is compiled against.
            return null;
        }

        return _candidates.TryGetValue(name, out string? path) ? LoadFromAssemblyPath(path) : null;
    }

    /// <summary>Detects <c>[ReferenceAssembly]</c> metadata-only assemblies without loading them.</summary>
    internal static bool IsReferenceAssembly(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader pe = new(stream);
            if (!pe.HasMetadata)
            {
                return true;
            }

            MetadataReader reader = pe.GetMetadataReader();
            foreach (CustomAttributeHandle handle in reader.GetAssemblyDefinition().GetCustomAttributes())
            {
                CustomAttribute attribute = reader.GetCustomAttribute(handle);
                if (attribute.Constructor.Kind == HandleKind.MemberReference)
                {
                    MemberReference ctor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                    if (ctor.Parent.Kind == HandleKind.TypeReference
                        && reader.GetString(reader.GetTypeReference((TypeReferenceHandle)ctor.Parent).Name) == "ReferenceAssemblyAttribute")
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (BadImageFormatException)
        {
            return true;
        }
    }
}
