using System.Text;
using HackerOs.Theming.Abstractions.IconPacks;

namespace HackerOs.Platform.Core.Tests.Appearance;

/// <summary>Icon pack folders, manifests and images (ADR 0203 §1 and §2).</summary>
public sealed class IconPackLibraryTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static byte[] Manifest(string id, string name = "Pack") =>
        Encoding.UTF8.GetBytes($$"""{"id":"{{id}}","name":"{{name}}","author":"Me","version":"1.0"}""");

    [Fact]
    public async Task ListAsync_reads_system_user_and_theme_pack_folders_in_order()
    {
        MemoryFiles files = new()
        {
            ["/usr/share/icons/neon/icon-pack.json"] = Manifest("neon", "Neon"),
            ["/usr/share/icons/hicolor/index.theme"] = [1],
            ["/usr/share/hackeros/theme-packs/retro/icons/icon-pack.json"] = Manifest("retro"),
            ["/home/alice/.local/share/icons/mine/icon-pack.json"] = Manifest("mine"),
        };

        IconPackListResult result = await IconPackLibrary.ListAsync(files.Access, "/home/alice");

        Assert.Equal(["neon", "retro", "mine"], result.Packs.Select(pack => pack.Id));
        Assert.Equal("/usr/share/hackeros/theme-packs/retro/icons", result.Packs[1].RootPath);
        Assert.Equal(IconPackOrigin.User, result.Packs[2].Origin);
        Assert.Equal("Neon", result.Find("neon")!.Name);
        // hicolor has no icon-pack.json: not a pack, and not a warning.
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task A_user_pack_cannot_hide_a_system_pack_with_the_same_id()
    {
        MemoryFiles files = new()
        {
            ["/usr/share/icons/neon/icon-pack.json"] = Manifest("neon"),
            ["/home/alice/.local/share/icons/neon/icon-pack.json"] = Manifest("neon"),
        };

        IconPackListResult result = await IconPackLibrary.ListAsync(files.Access, "/home/alice");

        Assert.Equal("/usr/share/icons/neon", Assert.Single(result.Packs).RootPath);
        Assert.Contains("already used", Assert.Single(result.Diagnostics), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"id":"builtin","name":"x"}""")]
    [InlineData("""{"id":"Bad Id","name":"x"}""")]
    [InlineData("""{"id":"ok"}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""not json""")]
    public void Invalid_manifests_are_refused(string json)
    {
        Assert.False(IconPackLibrary.TryParseManifest(json, "/usr/share/icons/x", IconPackOrigin.System, out IconPackInfo? info, out string? error));
        Assert.Null(info);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public async Task LoadAsync_keeps_real_pngs_only_and_turns_them_into_data_urls()
    {
        MemoryFiles files = new()
        {
            ["/usr/share/icons/p/icon-pack.json"] = Manifest("p"),
            ["/usr/share/icons/p/apps/org.hackeros.terminal.png"] = Png,
            ["/usr/share/icons/p/files/folder.png"] = Png,
            ["/usr/share/icons/p/files/evil.svg"] = Encoding.UTF8.GetBytes("<svg><script/></svg>"),
            ["/usr/share/icons/p/files/fake.png"] = Encoding.UTF8.GetBytes("not a png"),
            ["/usr/share/icons/p/files/huge.png"] = [.. Png, .. new byte[IconPackLibrary.MaxImageBytes]],
            ["/usr/share/icons/p/files/Upper.png"] = Png,
        };
        IconPackInfo info = Assert.Single((await IconPackLibrary.ListAsync(files.Access, null)).Packs);

        IconPackLoadResult loaded = await IconPackLibrary.LoadAsync(info, files.Access);

        Assert.Equal(["apps/org.hackeros.terminal.png", "files/folder.png"], loaded.Pack.Images.Keys.Order(StringComparer.Ordinal));
        Assert.StartsWith("data:image/png;base64,", loaded.Pack.FindApp("org.hackeros.terminal"), StringComparison.Ordinal);
        Assert.Equal(4, loaded.Diagnostics.Count);
    }

    [Fact]
    public void Lookups_follow_the_documented_precedence()
    {
        IconPack pack = new(
            new IconPackInfo("p", "P", null, null, null, "/usr/share/icons/p", IconPackOrigin.System),
            new Dictionary<string, string>
            {
                ["apps/org.hackeros.terminal.png"] = "data:app",
                ["categories/system.png"] = "data:category",
                ["files/folder.png"] = "data:folder",
                ["files/file.png"] = "data:file",
                ["files/txt.png"] = "data:txt",
            });

        Assert.Equal("data:app", pack.FindApp("org.hackeros.terminal", "utilities"));
        Assert.Equal("data:category", pack.FindApp("org.hackeros.settings", "System"));
        Assert.Null(pack.FindApp("org.other", "graphics"));
        Assert.Equal("data:folder", pack.FindFile(isDirectory: true, ".txt"));
        Assert.Equal("data:txt", pack.FindFile(isDirectory: false, ".TXT"));
        Assert.Equal("data:txt", pack.FindFile(isDirectory: false, "txt"));
        Assert.Equal("data:file", pack.FindFile(isDirectory: false, ".zip"));
        Assert.Equal("data:file", pack.FindFile(isDirectory: false, null));
    }

    /// <summary>An in-memory tree behind <see cref="IconPackFileAccess"/>.</summary>
    private sealed class MemoryFiles : Dictionary<string, byte[]>
    {
        public IconPackFileAccess Access => new(
            (directory, _) => Task.FromResult(List(directory, directories: true)),
            (directory, _) => Task.FromResult(List(directory, directories: false)),
            (path, _) => TryGetValue(path, out byte[]? bytes) ? Task.FromResult(bytes) : throw new IOException($"{path} not found"));

        private IReadOnlyList<string> List(string directory, bool directories)
        {
            string prefix = directory.TrimEnd('/') + "/";
            List<string> children = [.. Keys
                .Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
                .Select(path => path[prefix.Length..])
                .Select(rest => directories
                    ? (rest.Contains('/') ? prefix + rest[..rest.IndexOf('/')] : null)
                    : (rest.Contains('/') ? null : prefix + rest))
                .OfType<string>()
                .Distinct()];
            return children.Count == 0 && !Keys.Any(path => path.StartsWith(prefix, StringComparison.Ordinal))
                ? throw new IOException($"{directory} not found")
                : children;
        }
    }
}
