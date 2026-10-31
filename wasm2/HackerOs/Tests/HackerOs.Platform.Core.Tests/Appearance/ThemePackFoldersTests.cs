using System.Text;
using HackerOs.Theming.Abstractions;

namespace HackerOs.Platform.Core.Tests.Appearance;

/// <summary>The theme and cursor catalogs read the installed theme packs in place (ADR 0213 §3).</summary>
public sealed class ThemePackFoldersTests
{
    private const string Home = "/home/alice";
    private const string SystemPack = "/usr/share/hackeros/theme-packs/org.example.neon";
    private const string UserPack = "/home/alice/.local/share/hackeros/theme-packs/org.example.dusk";

    [Fact]
    public async Task The_themes_of_the_installed_packs_are_listed_with_their_origin()
    {
        FakeFiles files = new()
        {
            [$"{SystemPack}/themes/neon-night.theme"] = Theme("neon-night"),
            [$"{UserPack}/themes/dusk.theme"] = Theme("dusk"),
            ["/home/alice/.config/hackeros/themes/mine.theme"] = Theme("mine"),
        };

        UserThemeLibraryResult result = await UserThemeLibrary.LoadAsync(files.ListFilesAsync, files.ListDirectoriesAsync, files.ReadTextAsync, Home);

        Assert.Equal(ThemeOrigin.System, result.Themes.Single(theme => theme.Id == "neon-night").Origin);
        Assert.Equal(ThemeOrigin.User, result.Themes.Single(theme => theme.Id == "dusk").Origin);
        Assert.Contains(result.Themes, theme => theme.Id == "mine");
    }

    [Fact]
    public async Task Without_a_folder_lister_the_packs_are_not_read()
    {
        FakeFiles files = new() { [$"{SystemPack}/themes/neon-night.theme"] = Theme("neon-night") };

        UserThemeLibraryResult result = await UserThemeLibrary.LoadAsync(files.ListFilesAsync, files.ReadTextAsync, Home);

        Assert.Empty(result.Themes);
    }

    [Fact]
    public async Task A_pack_theme_cannot_take_the_id_of_a_system_theme()
    {
        FakeFiles files = new()
        {
            ["/etc/hackeros/themes/neon-night.theme"] = Theme("neon-night"),
            [$"{UserPack}/themes/neon-night.theme"] = Theme("neon-night"),
        };

        UserThemeLibraryResult result = await UserThemeLibrary.LoadAsync(files.ListFilesAsync, files.ListDirectoriesAsync, files.ReadTextAsync, Home);

        Assert.Equal("/etc/hackeros/themes/neon-night.theme", Assert.Single(result.Themes).SourcePath);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsError);
    }

    [Fact]
    public async Task The_cursor_themes_of_the_packs_come_after_their_place_and_a_pack_without_cursors_is_skipped()
    {
        FakeFiles files = new()
        {
            [$"{SystemPack}/cursors/cursor.theme"] = Cursor("neon-crypt"),
            [$"{SystemPack}/cursors/arrow.png"] = CursorThemeFileTests.Png(32, 32),
            [$"{UserPack}/themes/dusk.theme"] = Theme("dusk"),
            ["/home/alice/.config/hackeros/cursors/mine/cursor.theme"] = Cursor("mine"),
            ["/home/alice/.config/hackeros/cursors/mine/arrow.png"] = CursorThemeFileTests.Png(32, 32),
        };

        CursorThemeLibraryResult result = await CursorThemeLibrary.LoadAsync(files.ListDirectoriesAsync, files.ReadTextAsync, files.ReadBytesAsync, Home);

        Assert.Equal(["neon-crypt", "mine"], result.Themes.Select(theme => theme.Id));
        CursorTheme pack = result.Themes[0];
        Assert.Equal(ThemeOrigin.System, pack.Origin);
        Assert.Equal($"{SystemPack}/cursors/cursor.theme", pack.SourcePath);
        Assert.True(pack.Images.ContainsKey(CursorNames.Default));
    }

    private static byte[] Theme(string id) =>
        Encoding.UTF8.GetBytes($"[theme]\nid = {id}\nname = {id}\nbase = hackeros\n\n[tokens]\n--hos-accent = #ff00ff\n");

    private static byte[] Cursor(string id) =>
        Encoding.UTF8.GetBytes($"[cursor-theme]\nid = {id}\nname = {id}\n\n[cursors]\ndefault = arrow.png 2 1\n");

    /// <summary>An in-memory tree: files by absolute path, folders deduced from them.</summary>
    private sealed class FakeFiles : Dictionary<string, byte[]>
    {
        public FakeFiles()
            : base(StringComparer.Ordinal)
        {
        }

        public Task<IReadOnlyList<string>> ListFilesAsync(string directory, CancellationToken cancellationToken)
        {
            string prefix = directory.TrimEnd('/') + "/";
            List<string> found = [.. Keys.Where(path => path.StartsWith(prefix, StringComparison.Ordinal) && !path[prefix.Length..].Contains('/'))];
            return found.Count == 0 && !IsDirectory(prefix)
                ? Task.FromException<IReadOnlyList<string>>(new DirectoryNotFoundException(directory))
                : Task.FromResult<IReadOnlyList<string>>(found);
        }

        public Task<IReadOnlyList<string>> ListDirectoriesAsync(string directory, CancellationToken cancellationToken)
        {
            string prefix = directory.TrimEnd('/') + "/";
            if (!IsDirectory(prefix))
            {
                return Task.FromException<IReadOnlyList<string>>(new DirectoryNotFoundException(directory));
            }

            IReadOnlyList<string> folders = [.. Keys
                .Where(path => path.StartsWith(prefix, StringComparison.Ordinal) && path[prefix.Length..].Contains('/'))
                .Select(path => prefix + path[prefix.Length..].Split('/')[0])
                .Distinct(StringComparer.Ordinal)];
            return Task.FromResult(folders);
        }

        public Task<string> ReadTextAsync(string path, CancellationToken cancellationToken) =>
            TryGetValue(path, out byte[]? bytes)
                ? Task.FromResult(Encoding.UTF8.GetString(bytes))
                : Task.FromException<string>(new FileNotFoundException(path));

        public Task<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken) =>
            TryGetValue(path, out byte[]? bytes) ? Task.FromResult(bytes) : Task.FromException<byte[]>(new FileNotFoundException(path));

        private bool IsDirectory(string prefix) => Keys.Any(path => path.StartsWith(prefix, StringComparison.Ordinal));
    }
}
