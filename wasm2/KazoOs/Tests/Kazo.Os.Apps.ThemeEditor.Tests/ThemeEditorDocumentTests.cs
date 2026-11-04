using Kazo.Os.Theming.Abstractions;
using Xunit;

namespace Kazo.Os.Apps.ThemeEditor.Tests;

/// <summary>The Theme Editor's editing model (ADR 0103 §2).</summary>
public sealed class ThemeEditorDocumentTests
{
    [Theory]
    [InlineData("My Neon Night", "my-neon-night")]
    [InlineData("Été  rétro!!", "ete-retro")]
    [InlineData("--", "")]
    public void Slugify_builds_a_valid_file_name(string name, string expected) =>
        Assert.Equal(expected, ThemeEditorDocument.Slugify(name));

    [Fact]
    public void The_id_follows_the_name_until_edited_by_hand()
    {
        ThemeEditorDocument document = new() { Name = "Neon" };
        Assert.Equal("neon", document.Id);

        document.SetId("custom");
        document.Name = "Other";

        Assert.Equal("custom", document.Id);
    }

    [Fact]
    public void Invalid_values_are_kept_but_block_saving_and_stay_out_of_the_preview()
    {
        ThemeEditorDocument document = new();
        document.SetValue("--hos-accent", "#ff00ff");
        document.SetValue("--hos-bg-base", "url(http://evil)");

        Assert.Equal("url(http://evil)", document.GetValue("--hos-bg-base"));
        Assert.NotNull(document.GetError("--hos-bg-base"));
        Assert.Equal(["--hos-accent"], document.ValidTokens.Select(pair => pair.Key));
        Assert.Contains(document.GetProblems(), problem => problem.Contains("--hos-bg-base", StringComparison.Ordinal));

        document.SetValue("--hos-bg-base", "");
        Assert.Empty(document.GetProblems());
    }

    [Fact]
    public void A_built_in_id_or_no_platform_cannot_be_saved()
    {
        ThemeEditorDocument document = new();
        document.SetId(WellKnownThemeIds.Ubuntu);
        document.ForDesktop = false;

        IReadOnlyList<string> problems = document.GetProblems();

        Assert.Equal(2, problems.Count);
    }

    [Fact]
    public void Dirty_tracking_follows_load_edit_and_save()
    {
        UserTheme theme = ThemeFile.Parse("[theme]\nid = neon\nname = Neon\nbase = hackeros\n[tokens]\n--hos-accent = #ff00ff\n").Theme!;
        ThemeEditorDocument document = new();
        document.Load(theme, "/home/alice/.config/hackeros/themes/neon.theme");
        Assert.False(document.IsDirty);

        document.SetValue("--hos-accent", "#00ffff");
        Assert.True(document.IsDirty);

        UserTheme saved = document.ToUserTheme();
        document.MarkSaved("/home/alice/.config/hackeros/themes/neon.theme");
        Assert.False(document.IsDirty);
        Assert.Equal("#00ffff", saved.Tokens.Single().Value);
        Assert.Equal(theme.Tokens.Count, ThemeFile.Parse(ThemeFile.Serialize(saved)).Theme!.Tokens.Count);
    }

    [Fact]
    public void Reset_starts_from_the_base_theme_form_factor()
    {
        ThemeEditorDocument document = new();
        document.Reset(WellKnownThemeIds.Android);

        Assert.False(document.ForDesktop);
        Assert.True(document.ForMobile);
        Assert.Equal(0, document.TokenCount);
        Assert.False(document.IsDirty);
    }
}
