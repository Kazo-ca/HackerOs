using Kazo.Os.AppSdk.Files;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.Tests.IO;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.Files;

/// <summary>The desktop grid, shortcuts and settings file of ADR 0152 and ADR 0160.</summary>
public sealed class HackDesktopTests
{
    private static HackDesktopCell Cell(int column, int row) => new(column, row);

    [Fact]
    public void Order_puts_home_and_trash_first_then_folders_then_files_by_name()
    {
        IReadOnlyList<string> keys = HackDesktopLayout.Order(
            [
                new("b.txt", false, 10, DateTimeOffset.UnixEpoch),
                new("Zeta", true, 0, DateTimeOffset.UnixEpoch),
                new("a.txt", false, 99, DateTimeOffset.UnixEpoch)
            ],
            HackDesktopSort.Name,
            showHome: true,
            showTrash: true);

        Assert.Equal([HackDesktopItems.Home, HackDesktopItems.Trash, "Zeta", "a.txt", "b.txt"], keys);
    }

    [Fact]
    public void Order_by_date_is_newest_first_and_hides_system_icons_on_request()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        IReadOnlyList<string> keys = HackDesktopLayout.Order(
            [new("old.txt", false, 1, now), new("new.txt", false, 1, now.AddDays(1))],
            HackDesktopSort.Date,
            showHome: false,
            showTrash: true);

        Assert.Equal([HackDesktopItems.Trash, "new.txt", "old.txt"], keys);
    }

    [Fact]
    public void Arrange_keeps_saved_cells_and_gives_new_items_the_first_free_cells_down_a_column()
    {
        HackDesktopArrangement arrangement = HackDesktopLayout.Arrange(
            ["a", "b", "c"],
            new Dictionary<string, HackDesktopCell> { ["b"] = Cell(0, 0) },
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
        HackDesktopArrangement arrangement = HackDesktopLayout.Arrange(
            ["a", "b"],
            new Dictionary<string, HackDesktopCell> { ["a"] = Cell(0, 0), ["b"] = Cell(0, 0) },
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
        HackDesktopArrangement arrangement = HackDesktopLayout.Arrange(
            ["far"],
            new Dictionary<string, HackDesktopCell> { ["far"] = Cell(9, 1) },
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
        Dictionary<string, HackDesktopCell> saved = new() { ["a"] = Cell(1, 1) };

        HackDesktopArrangement arrangement = HackDesktopLayout.Arrange(["a"], saved, 3, 3, autoArrange: false, alignToGrid: true);

        Assert.False(arrangement.Changed);
    }

    [Fact]
    public void Auto_arrange_packs_items_in_the_order_of_their_saved_cells_then_new_ones()
    {
        HackDesktopArrangement arrangement = HackDesktopLayout.Arrange(
            ["new", "late", "early"],
            new Dictionary<string, HackDesktopCell> { ["late"] = Cell(3, 0), ["early"] = Cell(1, 1) },
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
        HackDesktopArrangement current = HackDesktopLayout.Arrange(
            ["a", "b", "c"],
            new Dictionary<string, HackDesktopCell> { ["a"] = Cell(0, 0), ["b"] = Cell(0, 1), ["c"] = Cell(2, 0) },
            columns: 4,
            rows: 3,
            autoArrange: false,
            alignToGrid: true);

        IReadOnlyDictionary<string, HackDesktopCell> positions = HackDesktopLayout.Move(current, ["a", "b"], "a", Cell(3, 1), 4, 3, autoArrange: false);

        Assert.Equal(Cell(3, 1), positions["a"]);
        Assert.Equal(Cell(3, 2), positions["b"]);
        Assert.Equal(Cell(2, 0), positions["c"]);

        IReadOnlyDictionary<string, HackDesktopCell> ontoOther = HackDesktopLayout.Move(current, ["a"], "a", Cell(2, 0), 4, 3, autoArrange: false);
        Assert.Equal(Cell(2, 1), ontoOther["a"]);
    }

    [Fact]
    public void Move_with_auto_arrange_inserts_the_dragged_items_at_the_target()
    {
        HackDesktopArrangement current = HackDesktopLayout.Arrange(
            ["a", "b", "c"],
            new Dictionary<string, HackDesktopCell> { ["a"] = Cell(0, 0), ["b"] = Cell(0, 1), ["c"] = Cell(0, 2) },
            columns: 2,
            rows: 3,
            autoArrange: true,
            alignToGrid: true);

        IReadOnlyDictionary<string, HackDesktopCell> positions = HackDesktopLayout.Move(current, ["c"], "c", Cell(0, 0), 2, 3, autoArrange: true);

        Assert.Equal(Cell(0, 0), positions["c"]);
        Assert.Equal(Cell(0, 1), positions["a"]);
        Assert.Equal(Cell(0, 2), positions["b"]);
    }

    [Fact]
    public void Grid_size_and_cell_at_point_follow_the_icon_size_and_stay_in_the_grid()
    {
        Assert.Equal((10, 7), HackDesktopLayout.GridSize(920, 700, HackDesktopIconSize.Medium));
        Assert.Equal((1, 1), HackDesktopLayout.GridSize(10, 10, HackDesktopIconSize.Large));
        Assert.Equal(Cell(1, 2), HackDesktopLayout.CellAtPoint(100, 250, HackDesktopIconSize.Medium, 10, 7));
        Assert.Equal(Cell(9, 0), HackDesktopLayout.CellAtPoint(5000, -40, HackDesktopIconSize.Medium, 10, 7));
    }

    [Theory]
    [InlineData("3,4", true, 3, 4)]
    [InlineData(" 0 , 12 ", true, 0, 12)]
    [InlineData("3", false, 0, 0)]
    [InlineData("a,b", false, 0, 0)]
    public void A_cell_reads_column_comma_row(string text, bool ok, int column, int row)
    {
        Assert.Equal(ok, HackDesktopCell.TryParse(text, out HackDesktopCell cell));
        if (ok)
        {
            Assert.Equal(Cell(column, row), cell);
            Assert.Equal($"{column},{row}", cell.ToString());
        }
    }

    [Fact]
    public void A_shortcut_round_trips_with_quoted_arguments()
    {
        HackDesktopEntry entry = new("My notes", "org.hackeros.text-editor", ["/home/alice/My notes.txt", "--line=3"], "file-text");

        string text = entry.Format();

        Assert.Contains("Exec=org.hackeros.text-editor \"/home/alice/My notes.txt\" --line=3", text, StringComparison.Ordinal);
        Assert.True(HackDesktopEntry.TryParse(text, out HackDesktopEntry? read));
        Assert.Equal(entry.Name, read.Name);
        Assert.Equal(entry.AppId, read.AppId);
        Assert.Equal(entry.Arguments, read.Arguments);
        Assert.Equal(entry.Icon, read.Icon);
    }

    [Fact]
    public void A_shortcut_drops_field_codes()
    {
        Assert.True(HackDesktopEntry.TryParse("[Desktop Entry]\nType=Application\nName=Editor\nExec=org.hackeros.text-editor %F 100%%\n", out HackDesktopEntry? entry));

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
        Assert.False(HackDesktopEntry.TryParse(content, out _));
    }

    [Fact]
    public void Is_desktop_file_needs_a_name_before_the_extension()
    {
        Assert.True(HackDesktopEntry.IsDesktopFile("/home/alice/Desktop/Editor.DESKTOP"));
        Assert.False(HackDesktopEntry.IsDesktopFile(".desktop"));
        Assert.False(HackDesktopEntry.IsDesktopFile("notes.txt"));
    }

    [Fact]
    public async Task Create_shortcut_numbers_the_file_when_the_name_is_taken()
    {
        using IDisposable scope = HackTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithDirectory("/home/alice/Desktop");

        string first = await HackDesktopEntry.CreateShortcutAsync("/home/alice/Desktop", "org.hackeros.calculator", "Calculator");
        string second = await HackDesktopEntry.CreateShortcutAsync("/home/alice/Desktop", "org.hackeros.calculator", "Calculator");

        Assert.Equal("/home/alice/Desktop/Calculator.desktop", first);
        Assert.Equal("/home/alice/Desktop/Calculator (2).desktop", second);
        Assert.True(HackDesktopEntry.TryParse(await HackFile.ReadAllTextAsync(second), out HackDesktopEntry? entry));
        Assert.Equal("org.hackeros.calculator", entry.AppId);
        await Assert.ThrowsAsync<ArgumentException>(() => HackDesktopEntry.CreateShortcutAsync("/home/alice/Desktop", "not an id", "X"));
    }

    [Fact]
    public void Settings_read_view_and_positions_and_ignore_bad_values()
    {
        HackDesktopSettingsDocument document = HackDesktopSettings.Parse(
            "[view]\nicon-size=large\nauto-arrange=true\nsort=bogus\nshow-trash=false\n\n[positions]\n::home=0,0\nnotes.txt=2,1\nbad=-1,0\nworse=x\n");

        Assert.Equal(HackDesktopIconSize.Large, document.Options.IconSize);
        Assert.True(document.Options.AutoArrange);
        Assert.Equal(HackDesktopSort.Name, document.Options.Sort);
        Assert.False(document.Options.ShowTrash);
        Assert.True(document.Options.ShowHome);
        Assert.Equal(2, document.Positions.Count);
        Assert.Equal(Cell(2, 1), document.Positions["notes.txt"]);
        Assert.Equal(HackDesktopOptions.Default, HackDesktopSettings.Parse(null).Options);
    }

    [Fact]
    public void Writing_options_keeps_comments_and_writing_positions_replaces_the_group()
    {
        string content = "# mine\n[view]\nicon-size=small\ncustom=1\n\n[positions]\nold.txt=5,5\n";

        string withOptions = HackDesktopSettings.WithOptions(content, HackDesktopOptions.Default with { Sort = HackDesktopSort.Date });
        string withPositions = HackDesktopSettings.WithPositions(withOptions, new Dictionary<string, HackDesktopCell> { ["a=b.txt"] = Cell(0, 1) });

        Assert.Contains("# mine", withPositions, StringComparison.Ordinal);
        Assert.Contains("custom=1", withPositions, StringComparison.Ordinal);
        Assert.Contains("sort=date", withPositions, StringComparison.Ordinal);
        Assert.DoesNotContain("old.txt", withPositions, StringComparison.Ordinal);
        HackDesktopSettingsDocument read = HackDesktopSettings.Parse(withPositions);
        Assert.Equal(HackDesktopIconSize.Medium, read.Options.IconSize);
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
        Assert.Equal(key, HackDesktopSettings.EncodeKey(name));
        Assert.True(HackDesktopSettings.TryDecodeKey(key, out string decoded));
        Assert.Equal(name, decoded);
    }

    [Fact]
    public void A_malformed_position_key_is_rejected()
    {
        Assert.False(HackDesktopSettings.TryDecodeKey("bad%2", out _));
        Assert.False(HackDesktopSettings.TryDecodeKey("bad%ZZ", out _));
    }

    [Fact]
    public async Task Saving_creates_the_config_directory_and_reads_back()
    {
        using IDisposable scope = HackTestHost.Enter(out FakeAppFileSystemGateway fs);

        await HackDesktopSettings.SaveOptionsAsync(HackDesktopOptions.Default with { IconSize = HackDesktopIconSize.Small });
        await HackDesktopSettings.SavePositionsAsync(new Dictionary<string, HackDesktopCell> { [HackDesktopItems.Trash] = Cell(0, 3) });

        Assert.True(fs.Exists("/home/alice/.config/hackeros/desktop.config"));
        HackDesktopSettingsDocument read = await HackDesktopSettings.ReadAsync();
        Assert.Equal(HackDesktopIconSize.Small, read.Options.IconSize);
        Assert.Equal(Cell(0, 3), read.Positions[HackDesktopItems.Trash]);
    }

    [Fact]
    public void App_icons_map_known_apps_and_fall_back_on_the_category()
    {
        Assert.False(string.IsNullOrEmpty(Kazo.Os.AppSdk.Apps.HackAppIcons.LucideName("org.unknown.thing", null)));
    }
}
