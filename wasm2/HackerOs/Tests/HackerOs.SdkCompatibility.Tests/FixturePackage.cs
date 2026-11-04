using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using HackerOs.App.Abstractions;

namespace HackerOs.SdkCompatibility.Tests;

/// <summary>
/// An app package of <c>Tests/fixtures/sdk-compat/packages</c> (ADR 0114): its generated manifests
/// and its assembly, loaded the way the OS loads a lazy app assembly, into a load context of its own
/// whose KazoOS references resolve to the SDK of this process (the current one).
/// </summary>
internal sealed class FixturePackage
{
    private FixturePackage(string fileName, IReadOnlyList<AppManifest> manifests, Assembly assembly, byte[] image)
    {
        FileName = fileName;
        Manifests = manifests;
        Assembly = assembly;
        Image = image;
    }

    /// <summary>Gets the package file name, e.g. <c>Contoso.Sdk1Compat.1.0.0.nupkg</c>.</summary>
    public string FileName { get; }

    /// <summary>Gets the manifests under <c>hackeros/manifests/</c>, parsed strictly by the current SDK.</summary>
    public IReadOnlyList<AppManifest> Manifests { get; }

    /// <summary>Gets the app assembly, loaded from the package.</summary>
    public Assembly Assembly { get; }

    /// <summary>Gets the raw bytes of the app assembly (for metadata inspection).</summary>
    public byte[] Image { get; }

    /// <summary>Gets every fixture package copied next to the tests.</summary>
    public static IEnumerable<string> All() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fixtures"), "*.nupkg").Order(StringComparer.Ordinal);

    /// <summary>Opens a fixture package.</summary>
    /// <param name="fileName">The package file name in the <c>fixtures</c> folder.</param>
    public static FixturePackage Open(string fileName)
    {
        using ZipArchive zip = ZipFile.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", fileName));
        List<AppManifest> manifests = [];
        byte[]? image = null;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (entry.FullName.StartsWith("hackeros/manifests/", StringComparison.Ordinal) && entry.FullName.EndsWith(".json", StringComparison.Ordinal))
            {
                using StreamReader reader = new(entry.Open());
                manifests.Add(AppManifestJsonSerializer.DeserializeStrict(reader.ReadToEnd()));
            }
            else if (entry.FullName.StartsWith("lib/net10.0/", StringComparison.Ordinal) && entry.FullName.EndsWith(".dll", StringComparison.Ordinal))
            {
                using MemoryStream bytes = new();
                entry.Open().CopyTo(bytes);
                image = bytes.ToArray();
            }
        }

        Assert.True(image is not null, $"{fileName} has no lib/net10.0 assembly.");
        AssemblyLoadContext context = new(fileName);
        Assembly assembly = context.LoadFromStream(new MemoryStream(image));
        return new FixturePackage(fileName, manifests, assembly, image);
    }
}
