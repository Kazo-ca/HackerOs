using System.IO.Compression;
using System.Text;
using HackerOs.Platform.Core.Apps.Packages;
using HackerOs.Platform.Core.Apps.ThemePacks;
using HackerOs.Platform.Core.Tests.Appearance;

namespace HackerOs.Platform.Core.Tests.Apps.ThemePacks;

/// <summary>The <c>.hacktheme</c> format and its "data only" checks (ADR 0190 §1, §3; ADR 0213 §1).</summary>
public sealed class ThemePackTests
{
    internal const string PackId = "org.example.neon";

    internal static string ThemeText(string id = "neon-night") =>
        $"[theme]\nid = {id}\nname = Neon Night\nbase = hackeros\n\n[tokens]\n--hos-accent = #ff00ff\n";

    /// <summary>A pack with every part: one theme, one wallpaper, an icon pack, a sound theme and a cursor theme.</summary>
    internal static Dictionary<string, byte[]> FullPack(string themeId = "neon-night") => new(StringComparer.Ordinal)
    {
        ["preview.png"] = CursorThemeFileTests.Png(64, 36),
        [$"themes/{themeId}.theme"] = Encoding.UTF8.GetBytes(ThemeText(themeId)),
        ["wallpapers/grid.png"] = CursorThemeFileTests.Png(64, 64),
        ["icons/icon-pack.json"] = Encoding.UTF8.GetBytes("""{"id":"neon-icons","name":"Neon icons"}"""),
        ["icons/apps/org.hackeros.terminal.png"] = CursorThemeFileTests.Png(32, 32),
        ["sounds/index.theme"] = Encoding.UTF8.GetBytes("[Sound Theme]\nName=Neon\nDirectories=stereo\n"),
        ["sounds/stereo/message-new-instant.oga"] = [.. "OggS"u8, 0, 0, 0, 0],
        ["cursors/cursor.theme"] = Encoding.UTF8.GetBytes("[cursor-theme]\nid = neon-crypt\nname = Neon Crypt\n\n[cursors]\ndefault = arrow.png 2 1\n"),
        ["cursors/arrow.png"] = CursorThemeFileTests.Png(32, 32),
    };

    /// <summary>Builds a <c>.hacktheme</c>: <c>hackeros-theme-pack.json</c> listing every file with its SHA-256.</summary>
    internal static byte[] Build(
        IReadOnlyDictionary<string, byte[]> files,
        string id = PackId,
        string version = "1.0.0",
        string name = "Neon",
        IReadOnlyDictionary<string, byte[]>? unlisted = null,
        string? overrideHash = null)
    {
        string listed = string.Join(',', files.Select(file => $"\"{file.Key}\":\"{overrideHash ?? AppPackage.Sha256(file.Value)}\""));
        byte[] manifest = Encoding.UTF8.GetBytes(
            $$$"""{"schemaVersion":1,"id":"{{{id}}}","version":"{{{version}}}","name":"{{{name}}}","author":"Example","files":{{{{listed}}}}}""");
        using MemoryStream archive = new();
        using (ZipArchive zip = new(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, byte[] bytes) in files.Prepend(new("hackeros-theme-pack.json", manifest)).Concat(unlisted ?? new Dictionary<string, byte[]>()))
            {
                using Stream stream = zip.CreateEntry(path).Open();
                stream.Write(bytes);
            }
        }

        return archive.ToArray();
    }

    [Fact]
    public void A_full_pack_is_read_with_every_part()
    {
        ThemePack pack = ThemePack.Read(Build(FullPack()));

        Assert.Equal(PackId, pack.Id);
        Assert.Equal("1.0.0", pack.Version.ToString());
        Assert.Equal("Neon", pack.Name);
        Assert.Equal("Example", pack.Author);
        Assert.Equal(
            ThemePackParts.Themes | ThemePackParts.Wallpapers | ThemePackParts.Icons | ThemePackParts.Sounds | ThemePackParts.Cursors,
            pack.Parts);
        Assert.Contains("cursors/arrow.png", pack.Files.Keys);
    }

    [Fact]
    public void A_pack_with_only_a_theme_is_accepted()
    {
        ThemePack pack = ThemePack.Read(Build(new Dictionary<string, byte[]> { ["themes/solo.theme"] = Encoding.UTF8.GetBytes(ThemeText("solo")) }));

        Assert.Equal(ThemePackParts.Themes, pack.Parts);
    }

    public static TheoryData<string, string, byte[]> RefusedFiles => new()
    {
        { "themes.pack.unexpected-file", "wallpapers/logo.svg", Encoding.UTF8.GetBytes("<svg onload=\"alert(1)\"/>") },
        { "themes.pack.unexpected-file", "scripts/run.js", Encoding.UTF8.GetBytes("alert(1)") },
        { "themes.pack.unexpected-file", "themes/nested/deep.theme", Encoding.UTF8.GetBytes(ThemeText("deep")) },
        { "themes.pack.bad-file", "wallpapers/fake.png", Encoding.UTF8.GetBytes("<svg/>") },
        { "themes.pack.remote-address", "themes/remote.theme", Encoding.UTF8.GetBytes(ThemeText("remote") + "--hos-wallpaper = url(https://evil.example/x.png)\n") },
        { "themes.pack.bad-file", "themes/broken.theme", Encoding.UTF8.GetBytes("[theme]\nid = broken\n") },
        { "themes.pack.unexpected-file", "Themes/Upper.theme", Encoding.UTF8.GetBytes(ThemeText("upper")) },
    };

    [Theory]
    [MemberData(nameof(RefusedFiles))]
    public void A_file_outside_the_rules_refuses_the_pack(string code, string path, byte[] content)
    {
        Dictionary<string, byte[]> files = new() { ["themes/ok.theme"] = Encoding.UTF8.GetBytes(ThemeText("ok")), [path] = content };

        AppPackageException error = Assert.Throws<AppPackageException>(() => ThemePack.Read(Build(files)));

        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void A_file_altered_after_listing_is_refused()
    {
        AppPackageException error = Assert.Throws<AppPackageException>(() => ThemePack.Read(Build(FullPack(), overrideHash: new string('0', 64))));

        Assert.Equal("themes.pack.hash-mismatch", error.Code);
    }

    [Fact]
    public void A_file_the_description_does_not_list_is_refused()
    {
        AppPackageException error = Assert.Throws<AppPackageException>(() => ThemePack.Read(Build(
            FullPack(), unlisted: new Dictionary<string, byte[]> { ["wallpapers/extra.png"] = CursorThemeFileTests.Png(8, 8) })));

        Assert.Equal("themes.pack.unlisted-file", error.Code);
    }

    [Fact]
    public void An_empty_pack_is_refused()
    {
        AppPackageException error = Assert.Throws<AppPackageException>(() => ThemePack.Read(Build(new Dictionary<string, byte[]>())));

        Assert.Equal("themes.pack.empty", error.Code);
    }

    [Theory]
    [InlineData("Bad Id")]
    [InlineData("single")]
    public void A_bad_pack_id_is_refused(string id)
    {
        AppPackageException error = Assert.Throws<AppPackageException>(() => ThemePack.Read(Build(FullPack(), id: id)));

        Assert.Equal("themes.pack.bad-description", error.Code);
    }

    [Fact]
    public void Parts_without_their_index_are_refused()
    {
        foreach (string index in new[] { "sounds/index.theme", "cursors/cursor.theme", "icons/icon-pack.json" })
        {
            Dictionary<string, byte[]> files = FullPack();
            files.Remove(index);

            AppPackageException error = Assert.Throws<AppPackageException>(() => ThemePack.Read(Build(files)));

            Assert.True(error.Code is "themes.pack.bad-file" or "themes.pack.unexpected-file", $"{index}: {error.Code}");
        }
    }

    [Fact]
    public void A_cursor_image_the_index_does_not_name_is_refused()
    {
        Dictionary<string, byte[]> files = FullPack();
        files["cursors/unused.png"] = CursorThemeFileTests.Png(32, 32);

        AppPackageException error = Assert.Throws<AppPackageException>(() => ThemePack.Read(Build(files)));

        Assert.Equal("themes.pack.unexpected-file", error.Code);
    }

    [Fact]
    public void A_sound_that_is_not_audio_is_refused()
    {
        Dictionary<string, byte[]> files = FullPack();
        files["sounds/stereo/message-new-instant.oga"] = CursorThemeFileTests.Png(8, 8);

        AppPackageException error = Assert.Throws<AppPackageException>(() => ThemePack.Read(Build(files)));

        Assert.Equal("themes.pack.bad-file", error.Code);
    }

    [Fact]
    public void A_wallpaper_over_5_mib_is_refused()
    {
        byte[] big = new byte[(5 * 1024 * 1024) + 1];
        CursorThemeFileTests.Png(8, 8).CopyTo(big, 0);

        AppPackageException error = Assert.Throws<AppPackageException>(() => ThemePack.Read(Build(new Dictionary<string, byte[]> { ["wallpapers/big.png"] = big })));

        Assert.Equal("themes.pack.file-too-large", error.Code);
    }

    [Fact]
    public void A_path_that_leaves_the_pack_is_refused()
    {
        AppPackageException error = Assert.Throws<AppPackageException>(() => ThemePack.Read(Build(
            new Dictionary<string, byte[]>(), unlisted: new Dictionary<string, byte[]> { ["../etc/passwd"] = [1] })));

        Assert.Equal("apps.package.unsafe-path", error.Code);
    }
}
