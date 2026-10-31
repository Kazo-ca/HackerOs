using System.Globalization;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Blazor.ContextMenus;
using HackerOs.AppSdk.Blazor.Files;
using HackerOs.AppSdk.Files;
using HackerOs.Platform.Blazor.ContextMenus;
using HackerOs.Platform.Core;
using HackerOs.Platform.Core.ContextMenus;
using HackerOs.Platform.Core.Lifecycle;
using HackerOs.Tests.Support;
using Microsoft.AspNetCore.Components;

namespace HackerOs.Platform.Blazor.Tests.ContextMenus;

/// <summary>
/// ADR 0151 §4 and ADR 0159 §5–§6: where the registered entries land in a file menu, which ones show,
/// what a click launches, and the context menu service adding them whichever app provides the control.
/// </summary>
public sealed class FileMenuEntriesTests
{
    private const string ZipperId = "com.example.zipper";
    private const string ToolsId = "com.example.tools";

    // ---- Placement ---------------------------------------------------------------------------------

    [Fact]
    public void First_after_afterGroup_and_unpositioned_entries_land_where_ADR_0151_says()
    {
        HackMenuDefinition menu = DefaultMenu();
        FileMenuEntryPlacement.Place(menu,
        [
            Candidate(ZipperId, Entry("top") with { First = true }),
            Candidate(ZipperId, Entry("extract") with { After = HackFileMenuItems.Open }),
            Candidate(ToolsId, Entry("hash") with { After = HackFileMenuItems.Open }),
            Candidate(ZipperId, Entry("pack") with { AfterGroup = HackFileMenuItems.Copy }),
            Candidate(ToolsId, Entry("send") with { AfterGroup = HackFileMenuItems.Copy }),
            Candidate(ToolsId, Entry("plain")),
            Candidate(ToolsId, Entry("lost") with { After = "not-in-this-menu" })
        ]);

        Assert.Equal(
            [
                ["app:com.example.zipper/top"],
                ["open", "app:com.example.zipper/extract", "app:com.example.tools/hash", "open-with"],
                ["cut", "copy"],
                ["app:com.example.zipper/pack", "app:com.example.tools/send"],
                ["app:com.example.tools/plain", "app:com.example.tools/lost"],
                ["properties"]
            ],
            Rows(menu));
    }

    [Fact]
    public void An_entry_anchored_to_a_later_entry_of_its_app_follows_it_and_the_common_group_ends_a_menu_without_properties()
    {
        HackMenuDefinition menu = new("files");
        menu.AddGroup().Add(HackFileMenuItems.Open, "Open");
        FileMenuEntryPlacement.Place(menu,
        [
            Candidate(ZipperId, Entry("second") with { After = "first" }),
            Candidate(ZipperId, Entry("first")),
            Candidate(ToolsId, Entry("orphan") with { After = "first" })
        ]);

        Assert.Equal(
            [["open"], ["app:com.example.zipper/first", "app:com.example.zipper/second", "app:com.example.tools/orphan"]],
            Rows(menu));
    }

    // ---- Source ------------------------------------------------------------------------------------

    [Fact]
    public async Task Apply_shows_matching_entries_hides_the_users_and_drops_an_empty_submenu()
    {
        Fixture fixture = await Fixture.CreateAsync(hidden: ["com.example.tools.hash"]);
        HackMenuDefinition menu = FileMenu(FileMenuScope.File, Item("/home/alice/a.zip", "application/zip"));

        await fixture.Source.ApplyAsync(menu);

        // "hash" is hidden; the "convert" submenu only holds image entries: it disappears for a zip.
        Assert.Equal(
            [
                ["open", "app:com.example.zipper/extract", "open-with"],
                ["cut", "copy"],
                ["properties"]
            ],
            Rows(menu));
        Assert.Equal("Extraire ici", menu.Find("app:com.example.zipper/extract")!.Title);
    }

    [Fact]
    public async Task Apply_builds_a_submenu_and_a_click_hands_the_paths()
    {
        Fixture fixture = await Fixture.CreateAsync();
        HackMenuDefinition menu = FileMenu(FileMenuScope.File, Item("/a.png", "image/png"), Item("/b.png", "image/png"));

        await fixture.Source.ApplyAsync(menu);

        HackMenuItemDefinition convert = menu.Find("app:com.example.tools/convert")!;
        HackMenuItemDefinition jpeg = Assert.Single(Assert.Single(convert.Children).Items);
        Assert.Equal("app:com.example.tools/to-jpeg", jpeg.Id);
        Assert.Null(menu.Find("app:com.example.zipper/extract"));

        await jpeg.OnClick.InvokeAsync(null);

        (string owner, AppContextMenuEntryManifest entry, string scope, IReadOnlyList<string> paths) = Assert.Single(fixture.Launches);
        Assert.Equal(ToolsId, owner);
        Assert.Equal("to-jpeg", entry.Name);
        Assert.Equal("File", scope);
        Assert.Equal(["/a.png", "/b.png"], paths);
    }

    [Fact]
    public async Task The_empty_space_hands_the_folder_shown()
    {
        Fixture fixture = await Fixture.CreateAsync();
        HackMenuDefinition menu = new("files") { FileMenu = new HackFileMenuContext(FileMenuScope.Desktop, [], "/home/alice/Desktop") };
        menu.AddGroup().Add(HackFileMenuItems.Paste, "Paste");

        await fixture.Source.ApplyAsync(menu);
        await menu.Find("app:com.example.tools/terminal-here")!.OnClick.InvokeAsync(null);

        Assert.Equal(["/home/alice/Desktop"], Assert.Single(fixture.Launches).Paths);
        Assert.Equal("Desktop", fixture.Launches[0].Scope);
    }

    [Fact]
    public void A_vector_icon_keeps_its_library_and_an_image_is_served_from_the_apps_assets()
    {
        Assert.Equal("_content/HackerOs.Apps.Zipper/icons/zip.svg",
            FileMenuEntries.IconOf("HackerOs.Apps.Zipper", new AppContextMenuIconManifest(AppContextMenus.ImageIcon, "icons/zip.svg"))!.Source);
        Assert.Equal("https://example.com/a.png",
            FileMenuEntries.IconOf("HackerOs.Apps.Zipper", new AppContextMenuIconManifest(AppContextMenus.ImageIcon, "https://example.com/a.png"))!.Source);
        Assert.Null(FileMenuEntries.IconOf("x", null));
    }

    [Fact]
    public async Task One_screen_shows_the_plain_entry_and_several_screens_the_submenu_of_the_open_screens()
    {
        // ADR 0218 §2: the entries of « Set as wallpaper », filtered by the screens open at the moment.
        AppContextMenuEntryManifest[] wallpaper =
        [
            Entry("wall") with { Types = ["image/*"], Screens = AppContextMenus.ScreensSingle },
            Entry("wall-on") with { Intent = null, Types = ["image/*"], Screens = AppContextMenus.ScreensMultiple },
            Entry("wall-all") with { Parent = "wall-on" },
            Entry("wall-2") with { Parent = "wall-on", Screen = 2 },
            Entry("wall-3") with { Parent = "wall-on", Screen = 3 }
        ];

        HackMenuDefinition single = FileMenu(FileMenuScope.File, Item("/a.png", "image/png"));
        await (await ScreenSourceAsync(wallpaper, 1)).ApplyAsync(single);
        Assert.NotNull(single.Find("app:com.example.tools/wall"));
        Assert.Null(single.Find("app:com.example.tools/wall-on"));

        HackMenuDefinition several = FileMenu(FileMenuScope.File, Item("/a.png", "image/png"));
        await (await ScreenSourceAsync(wallpaper, 1, 3)).ApplyAsync(several);
        Assert.Null(several.Find("app:com.example.tools/wall"));
        HackMenuItemDefinition submenu = several.Find("app:com.example.tools/wall-on")!;
        Assert.Equal(
            ["app:com.example.tools/wall-all", "app:com.example.tools/wall-3"],
            Assert.Single(submenu.Children).Items.Select(static item => item.Id));
    }

    [Fact]
    public void The_screen_filter_counts_screen_1_even_when_the_host_leaves_it_out()
    {
        AppContextMenuEntryManifest single = Entry("a") with { Screens = AppContextMenus.ScreensSingle };
        AppContextMenuEntryManifest multiple = Entry("b") with { Screens = AppContextMenus.ScreensMultiple };
        AppContextMenuEntryManifest unknown = Entry("c") with { Screens = "many" };

        Assert.True(FileContextMenuMatcher.MatchesScreens(single, new HashSet<int>()));
        Assert.True(FileContextMenuMatcher.MatchesScreens(single, new HashSet<int> { 1 }));
        Assert.False(FileContextMenuMatcher.MatchesScreens(single, new HashSet<int> { 2 }));
        Assert.True(FileContextMenuMatcher.MatchesScreens(multiple, new HashSet<int> { 2 }));
        Assert.False(FileContextMenuMatcher.MatchesScreens(unknown, new HashSet<int> { 1 }));
        Assert.True(FileContextMenuMatcher.MatchesScreens(Entry("d"), new HashSet<int> { 1, 2, 3 }));
    }

    private static async Task<FileMenuEntries> ScreenSourceAsync(AppContextMenuEntryManifest[] entries, params int[] screens)
    {
        AppCatalogBuildResult built = AppCatalog.Build([Manifest(ToolsId, entries)], []);
        Assert.True(built.IsSuccess, string.Join(", ", built.Errors.Select(static error => error.Message)));
        AppCatalog catalog = built.Catalog!;
        FakeAppFileSystemGateway files = new FakeAppFileSystemGateway()
            .WithDirectory(FileContextMenuRegistry.DirectoryPath)
            .WithFile(FileContextMenuRegistry.PathOf(ToolsId).Value, FileContextMenuRegistry.Serialize(
                new FileContextMenuDocument(1, ToolsId, "1.0.0", 1, FileContextMenuRegistry.SourceOf(entries), entries)));
        FileContextMenuRegistry registry = new(catalog, files);
        await registry.ReloadAsync();
        IReadOnlySet<string> none = new HashSet<string>();
        IReadOnlySet<int> open = screens.ToHashSet();
        return new FileMenuEntries(
            catalog,
            registry,
            new FileContextMenuLauncher(catalog, new AppEnablementRegistry(catalog), orchestrator: null),
            _ => ValueTask.FromResult(none),
            static () => CultureInfo.InvariantCulture,
            static (_, _, _, _) => Task.CompletedTask)
        {
            OpenScreens = () => open
        };
    }

    // ---- Context menu service ----------------------------------------------------------------------

    [Fact]
    public async Task The_service_adds_the_entries_after_the_providers_opening_and_survives_a_broken_source()
    {
        RecordingSource source = new();
        ContextMenuService service = new(fileMenuEntries: source);
        using ContextMenuScope scope = service.CreateScope(context: null);
        scope.Register(new HackMenuRegistration(
            "files",
            () => DefaultMenu(),
            new EventCallback<HackMenuOpeningEventArgs>(null, (Func<HackMenuOpeningEventArgs, Task>)(args =>
            {
                args.Menu.FileMenu = new HackFileMenuContext(FileMenuScope.File, [Item("/a.zip", "application/zip")], "/");
                return Task.CompletedTask;
            }))));
        scope.Register(new HackMenuRegistration("plain", () => DefaultMenu(), default));

        Assert.True(await scope.OpenAsync("files", null, 0, 0));
        Assert.Equal(1, source.Calls);
        Assert.NotNull(service.Current!.Root.Rows.FirstOrDefault(row => row.Item?.Id == "added"));

        Assert.True(await scope.OpenAsync("plain", null, 0, 0));
        Assert.Equal(1, source.Calls);

        source.Throw = true;
        Assert.True(await scope.OpenAsync("files", null, 0, 0));
        Assert.Null(service.Current!.Root.Rows.FirstOrDefault(row => row.Item?.Id == "added"));
    }

    private static HackMenuDefinition DefaultMenu()
    {
        HackMenuDefinition menu = new("files");
        HackMenuGroupDefinition open = menu.AddGroup();
        open.Add(HackFileMenuItems.Open, "Open");
        open.Add(HackFileMenuItems.OpenWith, "Open with");
        HackMenuGroupDefinition clipboard = menu.AddGroup();
        clipboard.Add(HackFileMenuItems.Cut, "Cut");
        clipboard.Add(HackFileMenuItems.Copy, "Copy");
        menu.AddGroup().Add(HackFileMenuItems.Properties, "Properties");
        return menu;
    }

    private static HackMenuDefinition FileMenu(FileMenuScope scope, params HackFileViewItem[] items)
    {
        HackMenuDefinition menu = DefaultMenu();
        menu.FileMenu = new HackFileMenuContext(scope, items, "/home/alice");
        return menu;
    }

    private static HackFileViewItem Item(string path, string mediaType) =>
        new(path, path[(path.LastIndexOf('/') + 1)..], false, 10, mediaType, DateTimeOffset.UnixEpoch);

    private static string[][] Rows(HackMenuDefinition menu) =>
        [.. menu.Groups.Select(static group => group.Items.Select(static item => item.Id).ToArray())];

    private static AppContextMenuEntryManifest Entry(string name) => new()
    {
        Name = name,
        Title = name,
        Titles = [new("en", name)],
        Intent = $"com.example.{name}.v1"
    };

    private static FileMenuEntryPlacement.Candidate Candidate(string appId, AppContextMenuEntryManifest entry) =>
        new(appId, entry, new HackMenuItemDefinition(FileMenuEntries.IdOf(appId, entry.Name), entry.Title));

    private static AppManifest Manifest(string id, IReadOnlyList<AppContextMenuEntryManifest> menus) => new()
    {
        Id = id,
        Name = id,
        Version = "1.0.0",
        PublisherId = "com.example",
        Description = "File menu fixture.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("HackerOs.Apps.Sample", "HackerOs.Apps.Sample.SampleApp"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Capabilities = [AppCapabilities.ShellContextMenu],
        ContextMenus = menus
    };

    /// <summary>Two apps whose files are already in <c>/etc/hackeros/context-menus.d</c>.</summary>
    private sealed class Fixture
    {
        internal List<(string Owner, AppContextMenuEntryManifest Entry, string Scope, IReadOnlyList<string> Paths)> Launches { get; } = [];

        internal FileMenuEntries Source { get; private set; } = null!;

        internal static async Task<Fixture> CreateAsync(params string[] hidden)
        {
            AppContextMenuEntryManifest[] zipper =
            [
                Entry("extract") with
                {
                    Types = [".zip"],
                    After = HackFileMenuItems.Open,
                    Titles = [new("en", "Extract here"), new("fr", "Extraire ici")]
                }
            ];
            AppContextMenuEntryManifest[] tools =
            [
                Entry("hash") with { Types = ["*"] },
                Entry("convert") with { Intent = null, Types = ["image/*"] },
                Entry("to-jpeg") with { Parent = "convert", Types = ["image/*"] },
                Entry("terminal-here") with { Scopes = [AppContextMenus.BackgroundScope] }
            ];
            AppCatalogBuildResult built = AppCatalog.Build([Manifest(ZipperId, zipper), Manifest(ToolsId, tools)], []);
            Assert.True(built.IsSuccess, string.Join(", ", built.Errors.Select(static error => error.Message)));
            AppCatalog catalog = built.Catalog!;

            FakeAppFileSystemGateway files = new FakeAppFileSystemGateway()
                .WithDirectory(FileContextMenuRegistry.DirectoryPath)
                .WithFile(FileContextMenuRegistry.PathOf(ZipperId).Value, FileContextMenuRegistry.Serialize(
                    new FileContextMenuDocument(1, ZipperId, "1.0.0", 1, FileContextMenuRegistry.SourceOf(zipper), zipper)))
                .WithFile(FileContextMenuRegistry.PathOf(ToolsId).Value, FileContextMenuRegistry.Serialize(
                    new FileContextMenuDocument(1, ToolsId, "1.0.0", 2, FileContextMenuRegistry.SourceOf(tools), tools)));
            FileContextMenuRegistry registry = new(catalog, files);
            await registry.ReloadAsync();
            Assert.Equal(2, registry.Apps.Count);

            Fixture fixture = new();
            IReadOnlySet<string> hiddenSet = hidden.ToHashSet(StringComparer.Ordinal);
            fixture.Source = new FileMenuEntries(
                catalog,
                registry,
                new FileContextMenuLauncher(catalog, new AppEnablementRegistry(catalog), orchestrator: null),
                _ => ValueTask.FromResult(hiddenSet),
                static () => CultureInfo.GetCultureInfo("fr-FR"),
                (owner, entry, scope, paths) =>
                {
                    fixture.Launches.Add((owner, entry, scope, paths));
                    return Task.CompletedTask;
                });
            return fixture;
        }
    }

    /// <summary>Adds one entry, or throws, and counts the calls.</summary>
    private sealed class RecordingSource : IFileMenuEntrySource
    {
        public int Calls { get; private set; }

        public bool Throw { get; set; }

        public Task ApplyAsync(HackMenuDefinition menu, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Throw)
            {
                // Half-applied before failing: the service must still open the menu.
                throw new InvalidOperationException("broken registration");
            }

            menu.AddGroup().Add("added", "Added");
            return Task.CompletedTask;
        }
    }
}
