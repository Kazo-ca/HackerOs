using System.Reflection;
using HackerOs.Theming.Abstractions.IconPacks;

namespace HackerOs.Platform.Core.FileSystem;

/// <summary>
/// The icon packs shipped with the OS (ADR 0203 §6), embedded in this assembly and copied by
/// <see cref="FileSystemSeeder"/> into <c>/usr/share/icons/</c>, as a distribution installs its icon
/// themes: they are ordinary root-owned files that anyone can read, copy or use as a model.
/// </summary>
public static class BuiltInIconPacks
{
    private const string ResourcePrefix = "HackerOs.IconPacks/";

    /// <summary>One file of a shipped pack.</summary>
    /// <param name="RelativePath">Path under <see cref="IconPackLibrary.SystemDirectory"/>, such as <c>neon/files/folder.png</c>.</param>
    /// <param name="Content">The file's bytes.</param>
    public sealed record PackFile(string RelativePath, byte[] Content);

    /// <summary>Gets every file of every shipped pack, by relative path.</summary>
    public static IReadOnlyList<PackFile> Files { get; } = Load();

    private static PackFile[] Load()
    {
        Assembly assembly = typeof(BuiltInIconPacks).Assembly;
        return [.. assembly.GetManifestResourceNames()
            .Where(static name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                using Stream stream = assembly.GetManifestResourceStream(name)!;
                using MemoryStream buffer = new();
                stream.CopyTo(buffer);
                // A Windows build writes RecursiveDir with backslashes.
                return new PackFile(name[ResourcePrefix.Length..].Replace('\\', '/'), buffer.ToArray());
            })];
    }
}
