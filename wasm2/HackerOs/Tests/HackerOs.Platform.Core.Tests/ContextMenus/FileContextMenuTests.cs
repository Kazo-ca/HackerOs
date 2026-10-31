using System.Globalization;
using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.AppSdk.Administration;
using HackerOs.Platform.Core.Administration;
using HackerOs.Platform.Core.ContextMenus;
using HackerOs.Platform.Core.Lifecycle;
using HackerOs.Platform.Core.Policy;
using HackerOs.Platform.Core.Tests.Settings;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Tests.ContextMenus;

/// <summary>
/// ADR 0151 and ADR 0159: the OS side of the file menu entries apps register — the folder
/// <c>/etc/hackeros/context-menus.d</c>, the filters, the launches, the user's hidden entries and
/// <c>HackFileMenuEntries</c>.
/// </summary>
public sealed class FileContextMenuTests
{
    private const string ZipperId = "com.example.zipper";
    private const string ViewerId = "com.example.viewer";
    private const string SettingsId = "org.hackeros.settings";
    private const string ExtractIntent = "com.example.zipper.extract.v1";
    private const string ViewIntent = "com.example.viewer.view.v1";

    private static readonly AppContextMenuEntryManifest Extract = Entry("extract", "Extract here", ExtractIntent) with
    {
        Types = [".zip"],
        After = "open",
        Titles = [new("en", "Extract here"), new("fr", "Extraire ici")]
    };

    // ---- Registry ----------------------------------------------------------------------------------

    [Fact]
    public async Task Sync_writes_one_file_per_app_in_activation_order_and_reads_it_back()
    {
        Harness harness = await Harness.CreateAsync();

        await harness.Registry.SyncAsync();

        // The catalog's activation order (by id) gives the first ranks.
        Assert.Equal([ViewerId, ZipperId], harness.Registry.Apps.Select(static app => app.AppId));
        Assert.Equal([1, 2], harness.Registry.Apps.Select(static app => app.Rank));
        string? json = await harness.Files.ReadAsync($"{FileContextMenuRegistry.DirectoryPath}/{ZipperId}.json");
        FileContextMenuDocument document = Assert.IsType<FileContextMenuDocument>(FileContextMenuRegistry.TryDeserialize(json!));
        Assert.Equal("1.0.0", document.AppVersion);
        Assert.Equal(FileContextMenuRegistry.SourceOf(document.Entries), document.Source);
        Assert.Equal("extract", Assert.Single(document.Entries).Name);
    }

    [Fact]
    public async Task Sync_keeps_an_unchanged_file_and_the_rank_of_a_changed_one()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.Registry.SyncAsync();
        string path = $"{FileContextMenuRegistry.DirectoryPath}/{ViewerId}.json";
        string? first = await harness.Files.ReadAsync(path);

        await harness.Registry.SyncAsync();
        Assert.Equal(first, await harness.Files.ReadAsync(path));

        // A file edited by hand to another source is rewritten from the manifest, keeping its rank.
        FileContextMenuDocument edited = FileContextMenuRegistry.TryDeserialize(first!)! with { Rank = 7, Source = "stale" };
        await harness.Files.WriteAsRootAsync(path, FileContextMenuRegistry.Serialize(edited));
        await harness.Registry.SyncAsync();

        FileContextMenuDocument rewritten = FileContextMenuRegistry.TryDeserialize((await harness.Files.ReadAsync(path))!)!;
        Assert.Equal(7, rewritten.Rank);
        Assert.NotEqual("stale", rewritten.Source);
        Assert.Equal([ZipperId, ViewerId], harness.Registry.Apps.Select(static app => app.AppId));
        Assert.Equal([2, 7], harness.Registry.Apps.Select(static app => app.Rank));
    }

    [Fact]
    public async Task Sync_removes_the_file_of_an_app_uninstalled_for_the_machine_or_unknown()
    {
        HashSet<string> uninstalled = new(StringComparer.Ordinal);
        Harness harness = await Harness.CreateAsync(uninstalled.Contains);
        await harness.Registry.SyncAsync();
        await harness.Files.WriteAsRootAsync($"{FileContextMenuRegistry.DirectoryPath}/com.example.gone.json",
            FileContextMenuRegistry.Serialize(new FileContextMenuDocument(1, "com.example.gone", "1.0.0", 9, "x", [Extract])));

        uninstalled.Add(ViewerId);
        await harness.Registry.SyncAsync();

        Assert.Equal([ZipperId], harness.Registry.Apps.Select(static app => app.AppId));
        Assert.Null(await harness.Files.ReadAsync($"{FileContextMenuRegistry.DirectoryPath}/{ViewerId}.json"));
        Assert.Null(await harness.Files.ReadAsync($"{FileContextMenuRegistry.DirectoryPath}/com.example.gone.json"));
    }

    [Fact]
    public async Task Reload_ignores_an_invalid_file_and_a_file_naming_another_app()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.Registry.SyncAsync();
        await harness.Files.WriteAsRootAsync($"{FileContextMenuRegistry.DirectoryPath}/{ZipperId}.json", "{ not json");
        FileContextMenuDocument viewer = FileContextMenuRegistry.TryDeserialize(
            (await harness.Files.ReadAsync($"{FileContextMenuRegistry.DirectoryPath}/{ViewerId}.json"))!)!;
        await harness.Files.WriteAsRootAsync(
            $"{FileContextMenuRegistry.DirectoryPath}/{ViewerId}.json",
            FileContextMenuRegistry.Serialize(viewer with { AppId = ZipperId }));

        await harness.Registry.ReloadAsync();

        Assert.Empty(harness.Registry.Apps);
    }

    [Fact]
    public async Task Reload_ignores_a_file_breaking_a_rule_of_the_manifest()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.Registry.SyncAsync();
        string path = $"{FileContextMenuRegistry.DirectoryPath}/{ZipperId}.json";
        FileContextMenuDocument zipper = FileContextMenuRegistry.TryDeserialize((await harness.Files.ReadAsync(path))!)!;
        await harness.Files.WriteAsRootAsync(path, FileContextMenuRegistry.Serialize(zipper with { Entries = [Extract with { Parent = "missing" }] }));

        await harness.Registry.ReloadAsync();

        Assert.Equal([ViewerId], harness.Registry.Apps.Select(static app => app.AppId));
    }

    // ---- Matcher -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("file", "/home/alice/a.zip", false, null, true)]
    [InlineData("file", "/home/alice/a.ZIP", false, null, true)]
    [InlineData("file", "/home/alice/a.txt", false, "text/plain", false)]
    [InlineData("directory", "/home/alice/docs", true, null, false)]
    [InlineData("background", "", false, null, false)]
    public void Matcher_applies_the_scope_and_the_types(string scope, string path, bool isDirectory, string? mediaType, bool expected)
    {
        FileMenuTarget[] targets = path.Length == 0 ? [] : [new(path, isDirectory, mediaType)];

        Assert.Equal(expected, FileContextMenuMatcher.Matches(Extract, scope, targets));
    }

    [Fact]
    public void Matcher_needs_every_item_accepted_and_single_hides_on_several()
    {
        AppContextMenuEntryManifest both = Entry("view", "View", ViewIntent) with { Scopes = ["file", "directory"] };
        FileMenuTarget file = new("/a.png", false, "image/png");
        FileMenuTarget folder = new("/docs", true, null);

        Assert.True(FileContextMenuMatcher.Matches(both, "file", [file, folder]));
        Assert.False(FileContextMenuMatcher.Matches(Extract, "file", [new("/a.zip", false, null), file]));
        Assert.False(FileContextMenuMatcher.Matches(both with { Selection = "single" }, "file", [file, file]));
        Assert.True(FileContextMenuMatcher.Matches(both with { Types = ["image/*"] }, "file", [file]));
        Assert.False(FileContextMenuMatcher.Matches(both with { Types = ["image/*"] }, "directory", [folder]));
        Assert.True(FileContextMenuMatcher.Matches(both with { Types = ["inode/directory"] }, "directory", [folder]));
    }

    [Fact]
    public void Matcher_shows_background_entries_on_the_desktop_but_not_the_reverse()
    {
        AppContextMenuEntryManifest background = Entry("new", "New", ViewIntent) with { Scopes = ["background"] };
        AppContextMenuEntryManifest desktop = Entry("wall", "Wallpaper", ViewIntent) with { Scopes = ["desktop"] };

        Assert.True(FileContextMenuMatcher.Matches(background, "desktop", []));
        Assert.True(FileContextMenuMatcher.Matches(desktop, "desktop", []));
        Assert.False(FileContextMenuMatcher.Matches(desktop, "background", []));
    }

    [Fact]
    public void Title_falls_back_from_the_culture_to_its_parent_then_english()
    {
        Assert.Equal("Extraire ici", FileContextMenuMatcher.TitleOf(Extract, CultureInfo.GetCultureInfo("fr-CA")));
        Assert.Equal("Extract here", FileContextMenuMatcher.TitleOf(Extract, CultureInfo.GetCultureInfo("de")));
    }

    // ---- Preferences -------------------------------------------------------------------------------

    [Fact]
    public void Preferences_edit_keeps_other_lines_and_the_last_line_wins()
    {
        string? text = FileContextMenuPreferences.Edit(null, "com.example.zipper.extract", hidden: true);
        Assert.StartsWith("# ~/.config/hackeros/context-menus.config", text, StringComparison.Ordinal);
        text += "# mine\n";
        text = FileContextMenuPreferences.Edit(text, "com.example.viewer.view", hidden: true);

        Assert.Equal(["com.example.viewer.view", "com.example.zipper.extract"], FileContextMenuPreferences.Parse(text).Order());

        text = FileContextMenuPreferences.Edit(text, "com.example.zipper.extract", hidden: false);
        Assert.Equal(["com.example.viewer.view"], FileContextMenuPreferences.Parse(text));
        Assert.Contains("# mine", text, StringComparison.Ordinal);
        Assert.Null(FileContextMenuPreferences.Edit(null, "a.b", hidden: false));
        Assert.Empty(FileContextMenuPreferences.Parse("[hidden]\na.b=true\na.b=false\n"));
    }

    [Fact]
    public async Task Preferences_are_the_session_users_file()
    {
        Harness harness = await Harness.CreateAsync();

        await harness.Preferences.SetHiddenAsync(ZipperId, "extract", hidden: true);

        Assert.Contains("com.example.zipper.extract=true", await harness.Files.ReadAsync("/home/alice/.config/hackeros/context-menus.config"));
        Assert.Equal(["com.example.zipper.extract"], await harness.Preferences.GetHiddenAsync());
    }

    // ---- Launcher ----------------------------------------------------------------------------------

    [Fact]
    public async Task Launcher_hands_the_intent_to_a_target_that_declares_it_and_arguments_otherwise()
    {
        Harness harness = await Harness.CreateAsync();
        FileContextMenuLauncher launcher = new(harness.Catalog, new AppEnablementRegistry(harness.Catalog), orchestrator: null);

        AppLaunchRequest declared = Assert.Single(launcher.BuildLaunches(ZipperId, Extract, "File", ["/a.zip", "/b.zip"], harness.Principal));
        Assert.Equal(ZipperId, declared.AppId);
        Assert.Empty(declared.Arguments);
        Assert.Equal(ExtractIntent, declared.Intent!.IntentId);
        Assert.Equal(FileContextMenuLauncher.ShellAppId, declared.Intent.SenderAppId);
        // ADR 0218 §3: the entry's name rides along, so one intent can serve several entries.
        Assert.Equal("""{"paths":["/a.zip","/b.zip"],"scope":"File","entry":"extract"}""", declared.Intent.PayloadJson);
        Assert.Equal("extract", HackerOs.AppSdk.Files.HackFileMenuIntent.ReadEntry(declared.Intent.PayloadJson));
        Assert.Null(HackerOs.AppSdk.Files.HackFileMenuIntent.ReadEntry(FileContextMenuLauncher.PayloadOf(["/a.zip"], "File")));

        AppContextMenuEntryManifest undeclared = Extract with { Intent = "com.example.zipper.other.v1", Selection = "each" };
        IReadOnlyList<AppLaunchRequest> each = launcher.BuildLaunches(ZipperId, undeclared, "File", ["/a.zip", "/b.zip"], harness.Principal);
        Assert.Equal([["com.example.zipper.other.v1", "/a.zip"], ["com.example.zipper.other.v1", "/b.zip"]], each.Select(static request => request.Arguments));
        Assert.All(each, static request => Assert.Null(request.Intent));
    }

    [Fact]
    public async Task Launcher_hides_an_entry_whose_other_target_does_not_declare_the_intent()
    {
        Harness harness = await Harness.CreateAsync();
        FileContextMenuLauncher launcher = new(harness.Catalog, new AppEnablementRegistry(harness.Catalog), orchestrator: null);

        Assert.True(launcher.IsAvailable(ZipperId, Extract));
        Assert.True(launcher.IsAvailable(ZipperId, Extract with { Target = ViewerId, Intent = ViewIntent }));
        Assert.False(launcher.IsAvailable(ZipperId, Extract with { Target = ViewerId }));
        Assert.False(launcher.IsAvailable(ZipperId, Extract with { Target = "com.example.missing" }));
        Assert.False(launcher.IsAvailable("com.example.missing", Extract));
    }

    // ---- HackFileMenuEntries -----------------------------------------------------------------------

    [Fact]
    public async Task Gateway_requires_apps_manage()
    {
        Harness harness = await Harness.CreateAsync();
        AppFileMenuEntryAdministrationGateway gateway = harness.CreateGateway();

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => gateway.GetEntriesAsync().AsTask());
        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => gateway.SetHiddenAsync(ZipperId, "extract", true).AsTask());
    }

    [Fact]
    public async Task Gateway_lists_the_entries_in_the_callers_language_and_hides_one()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.Registry.SyncAsync();
        AppFileMenuEntryAdministrationGateway gateway = harness.CreateGateway(AppCapabilities.AppsManage);

        await gateway.SetHiddenAsync(ZipperId, "extract", hidden: true);
        IReadOnlyList<HackFileMenuEntryInfo> entries = await gateway.GetEntriesAsync();

        Assert.Equal(["view", "extract"], entries.Select(static entry => entry.Name));
        HackFileMenuEntryInfo extract = entries[1];
        Assert.Equal("Extraire ici", extract.Title);
        Assert.Equal("Zipper", extract.AppName);
        Assert.True(extract.IsHidden);
        Assert.False(entries[0].IsHidden);
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.SetHiddenAsync(ZipperId, "missing", true).AsTask());

        // Showing a stale entry stays possible, to clean the line up.
        await gateway.SetHiddenAsync(ZipperId, "missing", hidden: false);
    }

    private static AppContextMenuEntryManifest Entry(string name, string title, string intent) => new()
    {
        Name = name,
        Title = title,
        Titles = [new("en", title)],
        Intent = intent
    };

    private static AppManifest Manifest(string id, string name, IReadOnlyList<AppContextMenuEntryManifest>? menus, params string[] intents) => new()
    {
        Id = id,
        Name = name,
        Version = "1.0.0",
        PublisherId = id.StartsWith("org.hackeros", StringComparison.Ordinal) ? "pub.hackeros" : "com.example",
        Description = "File menu fixture.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("HackerOs.Apps.Sample", "HackerOs.Apps.Sample.SampleApp"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Capabilities = menus is null ? [AppCapabilities.AppsManage] : [AppCapabilities.ShellContextMenu],
        Intents = [.. intents.Select(static intent => new AppIntentDeclarationManifest(intent))],
        ContextMenus = menus
    };

    /// <summary>Two apps with entries and Settings, for alice.</summary>
    private sealed class Harness
    {
        private Harness(SettingsFilesHarness files, AuthenticatedPrincipal principal, Func<string, bool>? uninstalled)
        {
            Files = files;
            Principal = principal;
            AppCatalogBuildResult built = AppCatalog.Build(
                [
                    Manifest(ZipperId, "Zipper", [Extract], ExtractIntent),
                    Manifest(ViewerId, "Viewer", [Entry("view", "View", ViewIntent) with { Scopes = ["file", "directory"] }], ViewIntent),
                    Manifest(SettingsId, "Settings", null)
                ],
                [SettingsId]);
            Assert.True(built.IsSuccess, string.Join(", ", built.Errors.Select(static error => error.Message)));
            Catalog = built.Catalog!;
            Registry = new FileContextMenuRegistry(Catalog, files.CreateRootGateway(), uninstalled);
            Preferences = new FileContextMenuPreferences(files.Settings);
        }

        internal SettingsFilesHarness Files { get; }
        internal AuthenticatedPrincipal Principal { get; }
        internal AppCatalog Catalog { get; }
        internal FileContextMenuRegistry Registry { get; }
        internal FileContextMenuPreferences Preferences { get; }

        internal static async Task<Harness> CreateAsync(Func<string, bool>? uninstalled = null)
        {
            SettingsFilesHarness files = new();
            await files.BootAsync();
            AuthenticatedPrincipal alice = await files.LoginAsync(files.Alice);
            return new Harness(files, alice, uninstalled);
        }

        internal AppFileMenuEntryAdministrationGateway CreateGateway(params string[] capabilities) =>
            new(() => new FileContextMenuAdministration(Registry, Preferences, Catalog),
                new FixedCapabilities(capabilities),
                static () => CultureInfo.GetCultureInfo("fr"));
    }

    /// <summary>Grants exactly the listed capabilities (the gateway only calls <see cref="Require"/>).</summary>
    private sealed class FixedCapabilities(params string[] granted) : ICapabilityChecker
    {
        public CapabilityPolicyEvaluation Evaluate(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null) =>
            throw new NotSupportedException();

        public void Require(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null)
        {
            if (!granted.Contains(capability, StringComparer.Ordinal))
            {
                throw new AppGatewayAccessDeniedException(capability, CapabilityPolicyEvaluation.DenyMissing(1));
            }
        }
    }
}
