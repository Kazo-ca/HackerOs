using System.Buffers.Binary;
using System.Globalization;
using HackerOs.Theming.Abstractions;

namespace HackerOs.Platform.Core.Tests.Appearance;

/// <summary>The cursor theme format, its image checks and the built-in themes (ADR 0205 §1, §2).</summary>
public sealed class CursorThemeFileTests
{
    /// <summary>Builds the start of a PNG file: signature and <c>IHDR</c> header, enough for the checks.</summary>
    internal static byte[] Png(int width, int height)
    {
        byte[] bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)height);
        bytes[24] = 8;
        bytes[25] = 6;
        return bytes;
    }

    [Fact]
    public void Parse_reads_the_identity_and_each_cursor_with_its_hot_spot()
    {
        CursorThemeParseResult result = CursorThemeFile.Parse(
            "# my cursors\n[cursor-theme]\nid = neon-crypt\nname = Neon Crypt\n\n[cursors]\ndefault = arrow.png 2 1\npointer = hand_1.png 11 0\n");

        CursorThemeIndex index = Assert.IsType<CursorThemeIndex>(result.Index);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("neon-crypt", index.Id);
        Assert.Equal("Neon Crypt", index.Name);
        Assert.Equal(
            [new CursorThemeEntry("default", "arrow.png", 2, 1, 7), new CursorThemeEntry("pointer", "hand_1.png", 11, 0, 8)],
            index.Entries);
    }

    [Theory]
    [InlineData("hand = hand.png 1 1", "Unknown cursor name 'hand'")]
    [InlineData("pointer = hand.png", "Expected 'pointer = file.png x y'")]
    [InlineData("pointer = hand.png -1 2", "Expected 'pointer = file.png x y'")]
    [InlineData("pointer = ../secret.png 1 1", "is not a PNG file name")]
    [InlineData("pointer = https://example.com/a.png 1 1", "is not a PNG file name")]
    [InlineData("pointer = hand.svg 1 1", "is not a PNG file name")]
    public void A_bad_cursor_line_is_ignored_with_a_warning(string line, string expected)
    {
        CursorThemeParseResult result = CursorThemeFile.Parse($"[cursor-theme]\nid = t\nname = T\n[cursors]\n{line}\ndefault = a.png 0 0\n");

        CursorThemeIndex index = Assert.IsType<CursorThemeIndex>(result.Index);
        Assert.Equal("default", Assert.Single(index.Entries).Name);
        ThemeFileDiagnostic warning = Assert.Single(result.Diagnostics);
        Assert.False(warning.IsError);
        Assert.Equal(5, warning.Line);
        Assert.Contains(expected, warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[cursor-theme]\nname = T\n", "Missing 'id' in [cursor-theme].")]
    [InlineData("[cursor-theme]\nid = Bad Id\nname = T\n", "Invalid id 'Bad Id'")]
    [InlineData("[cursor-theme]\nid = t\n", "Missing or invalid 'name' in [cursor-theme]")]
    public void A_missing_or_invalid_identity_refuses_the_theme(string content, string expected)
    {
        CursorThemeParseResult result = CursorThemeFile.Parse(content);

        Assert.Null(result.Index);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.Message.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Diagnostics_read_in_french()
    {
        ThemeFileDiagnostic diagnostic = Assert.Single(CursorThemeFile.Parse("[cursor-theme]\nid = t\nname = T\n[cursors]\nhand = a.png 0 0\n").Diagnostics);

        Assert.Equal("Nom de curseur inconnu « hand » ; ligne ignorée.", diagnostic.GetMessage(CultureInfo.GetCultureInfo("fr-FR")));
    }

    [Theory]
    [InlineData(32, 32, 2, 1, true, null)]
    [InlineData(128, 128, 127, 127, true, null)]
    [InlineData(129, 32, 0, 0, false, "Cursor.ImageTooBig")]
    [InlineData(32, 32, 32, 0, false, "Cursor.HotspotOutside")]
    [InlineData(0, 32, 0, 0, false, "Cursor.ImageTooBig")]
    public void An_image_is_checked_from_its_png_header(int width, int height, int x, int y, bool accepted, string? reason)
    {
        Assert.Equal(accepted, CursorThemeFile.TryValidateImage(Png(width, height), x, y, out string? reasonKey));
        Assert.Equal(reason, reasonKey);
    }

    [Fact]
    public void An_image_that_is_not_a_png_or_too_large_is_refused()
    {
        Assert.False(CursorThemeFile.TryValidateImage("<svg onload=alert(1)></svg>"u8, 0, 0, out string? notPng));
        Assert.Equal("Cursor.NotPng", notPng);

        byte[] huge = new byte[CursorThemeFile.MaxImageBytes + 1];
        Png(32, 32).CopyTo(huge, 0);
        Assert.False(CursorThemeFile.TryValidateImage(huge, 0, 0, out string? tooLarge));
        Assert.Equal("Cursor.ImageTooLarge", tooLarge);
    }

    [Fact]
    public void A_missing_cursor_falls_back_like_an_x_cursor_theme_link()
    {
        CursorImage resize = new("data:image/png;base64,AA==", 16, 16);
        CursorImage arrow = new("data:image/png;base64,AB==", 2, 1);
        CursorTheme theme = new("t", "T", ThemeOrigin.User, "/x/cursor.theme",
            new Dictionary<string, CursorImage> { ["ns-resize"] = resize, ["default"] = arrow });

        Assert.Same(resize, theme.Resolve("row-resize"));
        Assert.Same(resize, theme.Resolve("n-resize"));
        Assert.Null(theme.Resolve("copy")); // An arrow would lose the meaning: the host's cursor stays.
        Assert.Null(theme.Resolve("pointer"));
        IReadOnlyDictionary<string, CursorImage> all = theme.ResolveAll();
        Assert.Equal(["default", "ns-resize", "n-resize", "s-resize", "row-resize"], all.Keys);
    }

    [Fact]
    public void The_system_theme_draws_nothing_and_an_unknown_id_resolves_to_it()
    {
        Assert.Empty(CursorThemeCatalog.System.ResolveAll());
        Assert.Same(CursorThemeCatalog.System, CursorThemeLibraryResult.Empty.Resolve("deleted-theme"));
        Assert.Equal(CursorThemeCatalog.NeonId, CursorThemeLibraryResult.Empty.Resolve(CursorThemeCatalog.NeonId).Id);
    }

    [Fact]
    public void Every_built_in_cursor_is_a_shipped_png_whose_hot_spot_is_inside()
    {
        string wwwroot = Path.Combine(FindSolutionDirectory(), "Platform", "HackerOs.Theming.Blazor", "wwwroot");
        foreach (CursorTheme theme in CursorThemeCatalog.Themes.Where(theme => theme.Id != CursorThemeCatalog.SystemId))
        {
            Assert.True(theme.Images.ContainsKey(CursorNames.Default), theme.Id);
            foreach ((string name, CursorImage image) in theme.Images)
            {
                Assert.True(CursorNames.IsKnown(name), name);
                Assert.StartsWith(CursorThemeCatalog.AssetRoot + "/" + theme.Id + "/", image.Url, StringComparison.Ordinal);
                string file = Path.Combine(wwwroot, image.Url["_content/HackerOs.Theming.Blazor/".Length..]);
                Assert.True(
                    CursorThemeFile.TryValidateImage(File.ReadAllBytes(file), image.HotspotX, image.HotspotY, out string? reason),
                    $"{file}: {reason}");
            }
        }
    }

    private static string FindSolutionDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HackerOs.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("HackerOs.sln not found above the test output.");
    }
}
