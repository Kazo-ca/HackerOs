using Kazo.Os.AppSdk.Files;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.Tests.IO;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.Files;

/// <summary>The desktop grid, shortcuts and settings file of ADR 0152 and ADR 0160.</summary>
public sealed class KazoDesktopTests
{
    private static KazoDesktopCell Cell(int column, int row) => new(column, row);

    [Fact]
    public void Order_puts_home_and_trash_first_then_folders_then_files_by_name()
    {
        IReadOnlyList<string> keys = KazoDesktopLayout.Order(
            [
                new("b.txt", false, 10, DateTimeOffset.UnixEpoch),
                new("Zeta", true, 0, DateTimeOffset.UnixEpoch),
                new("a.txt", false, 99, DateTimeOffset.UnixEpoch)
            ],
            KazoDesktopSort.Name,
            showHome: true,
            showTrash: true);

        Assert.Equal([KazoDesktopItems.Home, KazoDesktopItems.Trash, "Zeta", "a.txt", "b.txt"], keys);
    }

    [Fact]
    public void Order_by_date_is_newest_first_and_hides_system_icons_on_request()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        IReadOnlyList<string> keys = KazoDesktopLayout.Order(
            [new("old.txt", false, 1, now), new("new.txt", false, 1, now.AddDays(1))],
            KazoDesktopSort.Date,
            showHome: false,
            showTrash: true);

        Assert.Equal([KazoDesktopItems.Trash, "new.txt", "old.txt"], keys);
    }

    [Fact]
    public void Arrange_keeps_saved_cells_and_gives_new_items_the_first_free_cells_down_a_column()
    {
        KazoDesktopArrangement arrangement = KazoDesktopLayout.Arrange(
            ["a", "b", "c"],
            new Dictionary<string, KazoDesktopCell> { ["b"] = Cell(0, 0) },
            columns: 4,
            rows: 2,
            autoArrange: false,
            alignToGrid: true);

        Assert.Equal(Cell(0, 0), arrangement.Displayed["b"]);
        Assert.Equal(Cell(0, 1), arrangement.Displayed["a"]);
        Assert.Equal(Cell(1, 0), arrangement.Displayed["c"]);
        Assert.True(arrangement.Changed);
    }

    [Fact]
    public void Arrange_moves_the_second_item_saved_on_a_taken_cell()
    {
        KazoDesktopArrangement arrangement = KazoDesktopLayout.Arrange(
            ["a", "b"],
            new Dictionary<string, KazoDesktopCell> { ["a"] = Cell(0, 0), ["b"] = Cell(0, 0) },
            columns: 2,
            rows: 2,
            autoArrange: false,
            alignToGrid: true);

        Assert.Equal(Cell(0, 0), arrangement.Displayed["a"]);
        Assert.Equal(Cell(0, 1), arrangement.Positions["b"]);
    }

    [Fact]
    public void Arrange_shows_an_item_saved_off_screen_nearby_but_keeps_its_saved_position()
    {
        KazoDesktopArrangement arrangement = KazoDesktopLayout.Arrange(
            ["far"],
            new Dictionary<string, KazoDesktopCell> { ["far"] = Cell(9, 1) },
            columns: 3,
            rows: 2,
            autoArrange: false,
            alignToGrid: true);

        Assert.Equal(Cell(2, 1), arrangement.Displayed["far"]);
        Assert.Equal(Cell(9, 1), arrangement.Positions["far"]);
        Assert.False(arrangement.Changed);
    }

    [Fact]
    public void Arrange_without_change_reports_nothing_to_save()
    {
        Dictionary<string, KazoDesktopCell> saved = new() { ["a"] = Cell(1, 1) };

        KazoDesktopArrangement arrangement = KazoDesktopLayout.Arrange(["a"], saved, 3, 3, autoArrange: false, alignToGrid: true);

        Assert.False(arrangement.Changed);
    }

    [Fact]
    public void Auto_arrange_packs_items_in_the_order_of_their_saved_cells_then_new_ones()
    {
        KazoDesktopArrangement arrangement = KazoDesktopLayout.Arrange(
            ["new", "late", "early"],
            new Dictionary<string, KazoDesktopCell> { ["late"] = Cell(3, 0), ["early"] = Cell(1, 1) },
            columns: 4,
            rows: 2,
            autoArrange: true,
            alignToGrid: true);

        Assert.Equal(Cell(0, 0), arrangement.Displayed["early"]);
        Assert.Equal(Cell(0, 1), arrangement.Displayed["late"]);
        Assert.Equal(Cell(1, 0), arrangement.Displayed["new"]);
    }

    [Fact]
    public void Move_keeps_the_distance_between_dragged_items_and_skips_taken_cells()
    {
        KazoDesktopArrangement current = KazoDesktopLayout.Arrange(
            ["a", "b", "c"],
            new Dictionary<string, KazoDesktopCell> { ["a"] = Cell(0, 0), ["b"] = Cell(0, 1), ["c"] = Cell(2, 0) },
            columns: 4,
            rows: 3,
            autoArrange: false,
            alignToGrid: true);

        IReadOnlyDictionary<string, KazoDesktopCell> positions = KazoDesktopLayout.Move(current, ["a", "b"], "a", Cell(3, 1), 4, 3, autoArrange: false);

        Assert.Equal(Cell(3, 1), positions["a"]);
        Assert.Equal(Cell(3, 2), positions["b"]);
        Assert.Equal(Cell(2, 0), positions["c"]);

        IReadOnlyDictionary<string, KazoDesktopCell> ontoOther = KazoDesktopLayout.Move(current, ["a"], "a", Cell(2, 0), 4, 3, autoArrange: false);
        Assert.Equal(Cell(2, 1), ontoOther["a"]);
    }

    [Fact]
    public void Move_with_auto_arrange_inserts_the_dragged_items_at_the_target()
    {
        KazoDesktopArrangement current = KazoDesktopLayout.Arrange(
            ["a", "b", "c"],
            new Dictionary<string, KazoDesktopCell> { ["a"] = Cell(0, 0), ["b"] = Cell(0, 1), ["c"] = Cell(0, 2) },
            columns: 2,
            rows: 3,
            autoArrange: true,
            alignToGrid: true);

        IReadOnlyDictionary<string, KazoDesktopCell> positions = KazoDesktopLayout.Move(current, ["c"], "c", Cell(0, 0), 2, 3, autoArrange: true);

        Assert.Equal(Cell(0, 0), positions["c"]);
        Assert.Equal(Cell(0, 1), positions["a"]);
        Assert.Equal(Cell(0, 2), positions["b"]);
    }

    [Fact]
    public void Grid_size_and_cell_at_point_follow_the_icon_size_and_stay_in_the_grid()
    {
        Assert.Equal((10, 7), KazoDesktopLayout.GridSize(920, 700, KazoDesktopIconSize.Medium));
        Assert.Equal((1, 1), KazoDesktopLayout.GridSize(10, 10, KazoDesktopIconSize.Large));
        Assert.Equal(Cell(1, 2), KazoDesktopLayout.CellAtPoint(100, 250, KazoDesktopIconSize.Medium, 10, 7));
        Assert.Equal(Cell(9, 0), KazoDesktopLayout.CellAtPoint(5000, -40, KazoDesktopIconSize.Medium, 10, 7));
    }

    [Theory]
    [InlineData("3,4", true, 3, 4)]
    [InlineData(" 0 , 12 ", true, 0, 12)]
    [InlineData("3", false, 0, 0)]
    [InlineData("a,b", false, 0, 0)]
    public void A_cell_reads_column_comma_row(string text, bool ok, int column, int row)
    {
        Assert.Equal(ok, KazoDesktopCell.TryParse(text, out KazoDesktopCell cell));
        if (ok)
        {
            Assert.Equal(Cell(column, row), cell);
            Assert.Equal($"{column},{row}", cell.ToString());
        }
    }

    [Fact]
    public void A_shortcut_round_trips_with_quoted_arguments()
    {
        KazoDesktopEntry entry = new("My notes", "org.kazoos.text-editor", ["/home/alice/My notes.txt", "--line=3"], "file-text");

        string text = entry.Format();

        Assert.Contains("Exec=org.kazoos.text-editor \"/home/alice/My notes.txt\" --line=3", text, StringComparison.Ordinal);
        Assert.True(KazoDesktopEntry.TryParse(text, out KazoDesktopEntry? read));
        Assert.Equal(entry.Name, read.Name);
        Assert.Equal(entry.AppId, read.AppId);
        Assert.Equal(entry.Arguments, read.Arguments);
        Assert.Equal(entry.Icon, read.Icon);
    }

    [Fact]
    public void A_shortcut_drops_field_codes()
    {
        Assert.True(KazoDesktopEntry.TryParse("[Desktop Entry]\nType=Application\nName=Editor\nExec=org.kazoos.text-editor %F 100%%\n", out KazoDesktopEntry? entry));

        Assert.Equal(["100%"], entry.Arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[Other]\nType=Application\nName=A\nExec=org.a\n")]
    [InlineData("[Desktop Entry]\nType=Link\nName=A\nExec=org.a\n")]
    [InlineData("[Desktop Entry]\nType=Application\nExec=org.a\n")]
    [InlineData("[Desktop Entry]\nType=Application\nName=A\nExec=\"org.a\n")]
    [InlineData("[Desktop Entry]\nType=Application\nName=A\nExec=/bin/sh -c x\n")]
    public void A_file_that_is_not_a_shortcut_is_not_read_as_one(string content)
    {
        Assert.False(KazoDesktopEntry.TryParse(content, out _));
    }

    [Fact]
    public void Is_desktop_file_needs_a_name_before_the_extension()
    {
        Assert.True(KazoDesktopEntry.IsDesktopFile("/home/alice/Desktop/Editor.DESKTOP"));
        Assert.False(KazoDesktopEntry.IsDesktopFile(".desktop"));
        Assert.False(KazoDesktopEntry.IsDesktopFile("notes.txt"));
    }

    [Fact]
    public async Task Create_shortcut_numbers_the_file_when_the_name_is_taken()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithDirectory("/home/alice/Desktop");

        string first = await KazoDesktopEntry.CreateShortcutAsync("/home/alice/Desktop", "org.kazoos.calculator", "Calculator");
        string second = await KazoDesktopEntry.CreateShortcutAsync("/home/alice/Desktop", "org.kazoos.calculator", "Calculator");

        Assert.Equal("/home/alice/Desktop/Calculator.desktop", first);
        Assert.Equal("/home/alice/Desktop/Calculator (2).desktop", second);
        Assert.True(KazoDesktopEntry.TryParse(await KazoFile.ReadAllTextAsync(second), out KazoDesktopEntry? entry));
        Assert.Equal("org.kazoos.calculator", entry.AppId);
        await Assert.ThrowsAsync<ArgumentException>(() => KazoDesktopEntry.CreateShortcutAsync("/home/alice/Desktop", "not an id", "X"));
    }

    [Fact]
    public void Settings_read_view_and_positions_and_ignore_bad_values()
    {
        KazoDesktopSettingsDocument document = KazoDesktopSettings.Parse(
            "[view]\nicon-size=large\nauto-arrange=true\nsort=bogus\nshow-trash=false\n\n[positions]\n::home=0,0\nnotes.txt=2,1\nbad=-1,0\nworse=x\n");

        Assert.Equal(KazoDesktopIconSize.Large, document.Options.IconSize);
        Assert.True(document.Options.AutoArrange);
        Assert.Equal(KazoDesktopSort.Name, document.Options.Sort);
        Assert.False(document.Options.ShowTrash);
        Assert.True(document.Options.ShowHome);
        Assert.Equal(2, document.Positions.Count);
        Assert.Equal(Cell(2, 1), document.Positions["notes.txt"]);
        Assert.Equal(KazoDesktopOptions.Default, KazoDesktopSettings.Parse(null).Options);
    }

    [Fact]
    public void Writing_options_keeps_comments_and_writing_positions_replaces_the_group()
    {
        string content = "# mine\n[view]\nicon-size=small\ncustom=1\n\n[positions]\nold.txt=5,5\n";

        string withOptions = KazoDesktopSettings.WithOptions(content, KazoDesktopOptions.Default with { Sort = KazoDesktopSort.Date });
        string withPositions = KazoDesktopSettings.WithPositions(withOptions, new Dictionary<string, KazoDesktopCell> { ["a=b.txt"] = Cell(0, 1) });

        Assert.Contains("# mine", withPositions, StringComparison.Ordinal);
        Assert.Contains("custom=1", withPositions, StringComparison.Ordinal);
        Assert.Contains("sort=date", withPositions, StringComparison.Ordinal);
        Assert.DoesNotContain("old.txt", withPositions, StringComparison.Ordinal);
        KazoDesktopSettingsDocument read = KazoDesktopSettings.Parse(withPositions);
        Assert.Equal(KazoDesktopIconSize.Medium, read.Options.IconSize);
        Assert.Equal(Cell(0, 1), Assert.Single(read.Positions).Value);
        Assert.Equal("a=b.txt", read.Positions.Keys.Single());
    }

    [Theory]
    [InlineData("plain.txt", "plain.txt")]
    [InlineData("a=b;c#d", "a%3Db%3Bc%23d")]
    [InlineData(" lead", "%20lead")]
    [InlineData("100%", "100%25")]
    [InlineData("[x]", "%5Bx%5D")]
    public void Position_keys_encode_reserved_characters(string name, string key)
    {
        Assert.Equal(key, KazoDesktopSettings.EncodeKey(name));
        Assert.True(KazoDesktopSettings.TryDecodeKey(key, out string decoded));
        Assert.Equal(name, decoded);
    }

    [Fact]
    public void A_malformed_position_key_is_rejected()
    {
        Assert.False(KazoDesktopSettings.TryDecodeKey("bad%2", out _));
        Assert.False(KazoDesktopSettings.TryDecodeKey("bad%ZZ", out _));
    }

    [Fact]
    public async Task Saving_creates_the_config_directory_and_reads_back()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);

        await KazoDesktopSettings.SaveOptionsAsync(KazoDesktopOptions.Default with { IconSize = KazoDesktopIconSize.Small });
        await KazoDesktopSettings.SavePositionsAsync(new Dictionary<string, KazoDesktopCell> { [KazoDesktopItems.Trash] = Cell(0, 3) });

        Assert.True(fs.Exists("/home/alice/.config/kazoos/desktop.config"));
        KazoDesktopSettingsDocument read = await KazoDesktopSettings.ReadAsync();
        Assert.Equal(KazoDesktopIconSize.Small, read.Options.IconSize);
        Assert.Equal(Cell(0, 3), read.Positions[KazoDesktopItems.Trash]);
    }

    [Fact]
    public void App_icons_map_known_apps_and_fall_back_on_the_category()
    {
        Assert.False(string.IsNullOrEmpty(Kazo.Os.AppSdk.Apps.KazoAppIcons.LucideName("org.unknown.thing", null)));
    }
}
