using System.Text;

namespace Kazo.Os.Tools.ManifestGenerator;

/// <summary>
/// Writes generated manifests into an app project's source directory (ADR 0047 §2): one definition →
/// <c>manifest.json</c> at the project root; several → <c>manifests/&lt;appId&gt;.json</c>. A file is
/// rewritten only when its bytes differ, so an unchanged build never touches its timestamp, and stale
/// files of the other layout are removed.
/// </summary>
public static class ManifestWriter
{
    /// <summary>File name used when the project declares a single app.</summary>
    public const string SingleFileName = "manifest.json";

    /// <summary>Directory used when the project declares several apps.</summary>
    public const string MultiDirectoryName = "manifests";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Writes <paramref name="manifests"/> and returns the full path of every manifest file, sorted.</summary>
    /// <param name="projectDirectory">The app project directory.</param>
    /// <param name="manifests">Validated manifests from <see cref="AppDefinitionInspector"/>; at least one.</param>
    /// <returns>Full paths of the manifest files that now describe the project.</returns>
    public static IReadOnlyList<string> Write(string projectDirectory, IReadOnlyList<GeneratedManifest> manifests)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentNullException.ThrowIfNull(manifests);
        if (manifests.Count == 0)
        {
            throw new ArgumentException("At least one manifest is required.", nameof(manifests));
        }

        string singlePath = Path.Combine(projectDirectory, SingleFileName);
        string multiDirectory = Path.Combine(projectDirectory, MultiDirectoryName);
        List<string> written = [];

        if (manifests.Count == 1)
        {
            WriteIfChanged(singlePath, manifests[0].CanonicalJson);
            written.Add(Path.GetFullPath(singlePath));
            DeleteStale(multiDirectory, keep: []);
        }
        else
        {
            Directory.CreateDirectory(multiDirectory);
            foreach (GeneratedManifest manifest in manifests)
            {
                string path = Path.Combine(multiDirectory, manifest.Manifest.Id + ".json");
                WriteIfChanged(path, manifest.CanonicalJson);
                written.Add(Path.GetFullPath(path));
            }

            DeleteStale(multiDirectory, keep: written);
            if (File.Exists(singlePath))
            {
                File.Delete(singlePath);
            }
        }

        written.Sort(StringComparer.Ordinal);
        return written;
    }

    /// <summary>Writes <paramref name="content"/> as UTF-8 without BOM unless the file already holds those exact bytes.</summary>
    /// <param name="path">Target file.</param>
    /// <param name="content">File content.</param>
    /// <returns><see langword="true"/> when the file was (re)written.</returns>
    public static bool WriteIfChanged(string path, string content)
    {
        byte[] bytes = Utf8NoBom.GetBytes(content);
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            return false;
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, bytes);
        return true;
    }

    private static void DeleteStale(string directory, IReadOnlyCollection<string> keep)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (!keep.Contains(Path.GetFullPath(file), StringComparer.Ordinal))
            {
                File.Delete(file);
            }
        }

        if (!Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }
}
