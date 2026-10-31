using HackerOs.App.Abstractions;

namespace HackerOs.App.Abstractions.Tests;

/// <summary>ADR 0151 §5 and ADR 0159: the rules of the <c>contextMenus</c> entries of a manifest.</summary>
public sealed class AppManifestContextMenusTests
{
    [Fact]
    public void A_complete_set_of_entries_with_a_submenu_is_valid_and_round_trips()
    {
        AppManifest manifest = Manifest(
            Entry("extract") with
            {
                Types = [".zip", "application/zip"],
                After = "open",
                Icon = new AppContextMenuIconManifest(AppContextMenus.VectorIcon, "package-open", "lucide"),
                Titles = [new("fr", "Extraire ici"), new("en", "Extract here")]
            },
            Entry("convert") with { Intent = null, Scopes = ["file", "directory"], AfterGroup = "copy" },
            Entry("to-png") with { Parent = "convert", Scopes = ["file"], Selection = AppContextMenus.SelectionEach },
            Entry("terminal") with { Scopes = ["desktop", "background"], First = true, Target = "org.hackeros.terminal", Intent = "org.hackeros.terminal.open-here.v1" });

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);
        Assert.True(result.IsValid, string.Join(", ", result.Errors.Select(static error => error.Code)));

        string json = AppManifestJsonSerializer.SerializeCanonical(manifest);
        AppManifest read = AppManifestJsonSerializer.DeserializeStrict(json);

        Assert.Equal(["extract", "convert", "to-png", "terminal"], read.ContextMenus!.Select(static entry => entry.Name));
        // Canonical form: titles by culture, scopes in menu order, types sorted.
        Assert.Equal(["en", "fr"], read.ContextMenus![0].Titles.Select(static title => title.Culture));
        Assert.Equal([".zip", "application/zip"], read.ContextMenus![0].Types);
        Assert.Equal(["background", "desktop"], read.ContextMenus![3].Scopes);
        Assert.Equal(json, AppManifestJsonSerializer.SerializeCanonical(read));
    }

    [Fact]
    public void The_screen_filter_of_ADR_0218_is_valid_and_round_trips()
    {
        AppManifest manifest = Manifest(
            Entry("plain") with { Screens = AppContextMenus.ScreensSingle },
            Entry("menu") with { Intent = null, Screens = AppContextMenus.ScreensMultiple },
            Entry("screen-9") with { Parent = "menu", Screen = AppContextMenus.MaxScreenNumber });

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);
        Assert.True(result.IsValid, string.Join(", ", result.Errors.Select(static error => error.Code)));

        AppManifest read = AppManifestJsonSerializer.DeserializeStrict(AppManifestJsonSerializer.SerializeCanonical(manifest));
        Assert.Equal(["single", "multiple", null], read.ContextMenus!.Select(static entry => entry.Screens));
        Assert.Equal([null, null, 9], read.ContextMenus!.Select(static entry => entry.Screen));
    }

    [Fact]
    public void A_manifest_without_entries_writes_no_contextMenus_property()
    {
        string json = AppManifestJsonSerializer.SerializeCanonical(Manifest() with { ContextMenus = [] });

        Assert.DoesNotContain("contextMenus", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("manifest.context-menu.name.invalid")]
    [InlineData("manifest.context-menu.title.english")]
    [InlineData("manifest.context-menu.target.invalid")]
    [InlineData("manifest.context-menu.intent.required")]
    [InlineData("manifest.context-menu.intent.invalid")]
    [InlineData("manifest.context-menu.scope.invalid")]
    [InlineData("manifest.context-menu.selection.invalid")]
    [InlineData("manifest.context-menu.type.invalid")]
    [InlineData("manifest.context-menu.icon.invalid")]
    [InlineData("manifest.context-menu.position.many")]
    [InlineData("manifest.context-menu.anchor.unknown")]
    [InlineData("manifest.context-menu.screens.invalid")]
    [InlineData("manifest.context-menu.screen.invalid")]
    [InlineData("manifest.context-menu.screen.many")]
    public void Each_rule_of_one_entry_has_its_code(string code)
    {
        AppContextMenuEntryManifest entry = code switch
        {
            "manifest.context-menu.name.invalid" => Entry("Extract_Here"),
            "manifest.context-menu.title.english" => Entry("extract") with { Titles = [new("fr", "Extraire")] },
            "manifest.context-menu.target.invalid" => Entry("extract") with { Target = "not an app" },
            "manifest.context-menu.intent.required" => Entry("extract") with { Intent = null },
            "manifest.context-menu.intent.invalid" => Entry("extract") with { Target = "org.hackeros.terminal", Intent = "open" },
            "manifest.context-menu.scope.invalid" => Entry("extract") with { Scopes = ["everywhere"] },
            "manifest.context-menu.selection.invalid" => Entry("extract") with { Selection = "some" },
            "manifest.context-menu.type.invalid" => Entry("extract") with { Types = ["ZIP"] },
            "manifest.context-menu.icon.invalid" => Entry("extract") with { Icon = new AppContextMenuIconManifest(AppContextMenus.VectorIcon, "zip") },
            "manifest.context-menu.position.many" => Entry("extract") with { First = true, After = "open" },
            "manifest.context-menu.screens.invalid" => Entry("extract") with { Screens = "some" },
            "manifest.context-menu.screen.invalid" => Entry("extract") with { Screen = 1 },
            "manifest.context-menu.screen.many" => Entry("extract") with { Screens = AppContextMenus.ScreensMultiple, Screen = 2 },
            _ => Entry("extract") with { After = "nowhere" }
        };

        Assert.Contains(AppManifestValidator.Validate(Manifest(entry)).Errors, error => error.Code == code);
    }

    [Fact]
    public void Submenus_check_their_parent_scope_position_cycles_and_depth()
    {
        static string[] Codes(params AppContextMenuEntryManifest[] entries) =>
            [.. AppManifestValidator.Validate(Manifest(entries)).Errors.Select(static error => error.Code)];

        Assert.Contains("manifest.context-menu.parent.unknown", Codes(Entry("child") with { Parent = "missing" }));
        Assert.Contains("manifest.context-menu.parent.intent", Codes(Entry("parent"), Entry("child") with { Parent = "parent" }));
        Assert.Contains("manifest.context-menu.parent.scope",
            Codes(Entry("parent") with { Intent = null }, Entry("child") with { Parent = "parent", Scopes = ["directory"] }));
        Assert.Contains("manifest.context-menu.position.child",
            Codes(Entry("parent") with { Intent = null }, Entry("child") with { Parent = "parent", First = true }));
        Assert.Contains("manifest.context-menu.parent.cycle",
            Codes(Entry("a") with { Intent = null, Parent = "b" }, Entry("b") with { Intent = null, Parent = "a" }));
        Assert.Contains("manifest.context-menu.depth", Codes(
            Entry("l1") with { Intent = null },
            Entry("l2") with { Intent = null, Parent = "l1" },
            Entry("l3") with { Intent = null, Parent = "l2" },
            Entry("l4") with { Parent = "l3" }));
        Assert.Empty(Codes(
            Entry("l1") with { Intent = null },
            Entry("l2") with { Intent = null, Parent = "l1" },
            Entry("l3") with { Parent = "l2" }));
    }

    [Fact]
    public void Entries_need_the_capability_and_an_app_that_can_be_launched()
    {
        AppManifest manifest = Manifest(Entry("extract"));

        Assert.Contains(AppManifestValidator.Validate(manifest with { Capabilities = [] }).Errors,
            static error => error.Code == "manifest.context-menu.capability");
        Assert.Contains(AppManifestValidator.Validate(manifest with { Kind = AppKind.Terminal }).Errors,
            static error => error.Code == "manifest.context-menu.forbidden");
    }

    [Fact]
    public void The_capability_is_a_normal_one()
    {
        Assert.Contains(AppCapabilities.ShellContextMenu, AppCapabilities.All);
        Assert.Equal("shell.context-menu", AppCapabilities.ShellContextMenu);
    }

    private static AppContextMenuEntryManifest Entry(string name) => new()
    {
        Name = name,
        Title = $"Menu.{name}",
        Titles = [new("en", name)],
        Intent = $"com.example.zipper.{name}.v1"
    };

    private static AppManifest Manifest(params AppContextMenuEntryManifest[] entries) => new()
    {
        Id = "com.example.zipper",
        Name = "Zipper",
        Version = "1.0.0",
        PublisherId = "com.example",
        Description = "Zips files.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Example.Zipper", "Example.Zipper.ZipperApp"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("tools", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Capabilities = [AppCapabilities.ShellContextMenu],
        ContextMenus = entries.Length == 0 ? null : entries
    };
}
