using Kazo.Os.AppSdk.Blazor.ContextMenus;
using Kazo.Os.Platform.Blazor.ContextMenus;

namespace Kazo.Os.Platform.Blazor.Tests.ContextMenus;

/// <summary>ADR 0150 §3 and §6: what the OS draws of a menu, and how keys and the pointer move in it.</summary>
public sealed class ContextMenuLayoutTests
{
    [Fact]
    public void A_separator_is_drawn_only_between_two_visible_groups()
    {
        // The plan's file menu: "Open with" alone in its group; hidden, its separator goes too.
        KazoMenuDefinition menu = Menu(
            ["open"],
            ["open-with:hidden"],
            ["rename", "copy"],
            [],
            ["extra:hidden"],
            ["properties"]);

        ContextMenuLevel level = ContextMenuLayout.Arrange(menu.Groups)!;

        Assert.Equal(["open", "-", "rename", "copy", "-", "properties"], Describe(level));
    }

    [Fact]
    public void The_menu_never_starts_or_ends_with_a_separator()
    {
        KazoMenuDefinition menu = Menu(["first:hidden"], ["middle"], ["last:hidden"]);

        Assert.Equal(["middle"], Describe(ContextMenuLayout.Arrange(menu.Groups)!));
    }

    [Fact]
    public void Nothing_visible_draws_no_menu()
    {
        Assert.Null(ContextMenuLayout.Arrange(Menu(["a:hidden"], []).Groups));
    }

    [Fact]
    public void A_submenu_with_no_visible_entry_hides_its_parent_and_the_rule_applies_inside()
    {
        KazoMenuDefinition menu = new("files");
        KazoMenuGroupDefinition group = menu.AddGroup();
        KazoMenuItemDefinition empty = group.Add("empty", "Empty");
        empty.Children.Add(new KazoMenuGroupDefinition());
        empty.Children[0].Add("gone", "Gone").IsVisible = false;
        KazoMenuItemDefinition send = group.Add("send", "Send to");
        send.Children.Add(new KazoMenuGroupDefinition());
        send.Children[0].Add("desktop", "Desktop");
        send.Children.Add(new KazoMenuGroupDefinition());
        send.Children.Add(new KazoMenuGroupDefinition());
        send.Children[2].Add("mail", "Mail");

        ContextMenuLevel level = ContextMenuLayout.Arrange(menu.Groups)!;

        ContextMenuRow row = Assert.Single(level.Rows);
        Assert.Equal("send", row.Item!.Id);
        Assert.Equal(["desktop", "-", "mail"], Describe(row.Submenu!));
    }

    [Fact]
    public void Keys_skip_separators_and_disabled_entries_and_wrap_around()
    {
        KazoMenuDefinition menu = Menu(["open", "preview:disabled"], ["copy"]);
        ContextMenuNavigator navigator = new(ContextMenuLayout.Arrange(menu.Groups)!, highlightFirst: true);

        Assert.Equal(0, navigator.Active.Highlight);
        navigator.HandleKey("ArrowDown");
        Assert.Equal(3, navigator.Active.Highlight);
        navigator.HandleKey("ArrowDown");
        Assert.Equal(0, navigator.Active.Highlight);
        navigator.HandleKey("ArrowUp");
        Assert.Equal(3, navigator.Active.Highlight);
        navigator.HandleKey("Home");
        Assert.Equal(0, navigator.Active.Highlight);
        navigator.HandleKey("c");
        Assert.Equal(3, navigator.Active.Highlight);
    }

    [Fact]
    public void Right_opens_a_submenu_left_and_escape_close_it_and_enter_chooses()
    {
        KazoMenuDefinition menu = new("files");
        KazoMenuItemDefinition export = menu.AddGroup().Add("export", "Export");
        export.Children.Add(new KazoMenuGroupDefinition());
        export.Children[0].Add("csv", "CSV");
        export.Children[0].Add("json", "JSON");
        ContextMenuNavigator navigator = new(ContextMenuLayout.Arrange(menu.Groups)!, highlightFirst: true);

        navigator.HandleKey("ArrowRight");
        Assert.Equal(2, navigator.Levels.Count);
        Assert.Equal(0, navigator.Active.Highlight);
        navigator.HandleKey("ArrowLeft");
        Assert.Single(navigator.Levels);

        Assert.Equal(ContextMenuNavigation.None, navigator.HandleKey("Enter"));
        Assert.Equal(2, navigator.Levels.Count);
        navigator.HandleKey("ArrowDown");
        Assert.Equal(ContextMenuNavigation.Invoke, navigator.HandleKey("Enter"));
        Assert.Equal("json", navigator.Chosen!.Id);

        Assert.Equal(ContextMenuNavigation.None, navigator.HandleKey("Escape"));
        Assert.Single(navigator.Levels);
        Assert.Equal(ContextMenuNavigation.Close, navigator.HandleKey("Escape"));
        Assert.Equal(ContextMenuNavigation.Close, navigator.HandleKey("Tab"));
    }

    [Fact]
    public void Hovering_a_parent_keeps_its_open_submenu_and_another_row_closes_it()
    {
        KazoMenuDefinition menu = new("files");
        KazoMenuGroupDefinition group = menu.AddGroup();
        KazoMenuItemDefinition export = group.Add("export", "Export");
        export.Children.Add(new KazoMenuGroupDefinition());
        export.Children[0].Add("csv", "CSV");
        group.Add("rename", "Rename");
        ContextMenuNavigator navigator = new(ContextMenuLayout.Arrange(menu.Groups)!, highlightFirst: false);

        Assert.Equal(-1, navigator.Active.Highlight);
        navigator.Hover(0, 0);
        navigator.Hover(1, 0);
        navigator.Hover(0, 0);
        Assert.Equal(2, navigator.Levels.Count);
        Assert.Equal(0, navigator.Levels[1].Highlight);

        navigator.Hover(0, 1);
        Assert.Single(navigator.Levels);
        Assert.Equal(ContextMenuNavigation.Invoke, navigator.Click(0, 1));
        Assert.Equal("rename", navigator.Chosen!.Id);
    }

    /// <summary>Builds a menu from groups of ids; ":hidden" and ":disabled" suffixes set the flags.</summary>
    private static KazoMenuDefinition Menu(params string[][] groups)
    {
        KazoMenuDefinition menu = new("test");
        foreach (string[] ids in groups)
        {
            KazoMenuGroupDefinition group = menu.AddGroup();
            foreach (string spec in ids)
            {
                string[] parts = spec.Split(':');
                KazoMenuItemDefinition item = group.Add(parts[0], char.ToUpperInvariant(parts[0][0]) + parts[0][1..]);
                item.IsVisible = !parts.Contains("hidden");
                item.IsDisabled = parts.Contains("disabled");
            }
        }

        return menu;
    }

    private static string[] Describe(ContextMenuLevel level) =>
        [.. level.Rows.Select(row => row.IsSeparator ? "-" : row.Item!.Id)];
}
