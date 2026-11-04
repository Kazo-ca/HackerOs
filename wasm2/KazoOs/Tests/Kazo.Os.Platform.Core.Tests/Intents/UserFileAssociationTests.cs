using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Intents;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Intents;

/// <summary>
/// The user layer of the file associations (ADR 0121): <c>~/.config/hackeros/file-associations.json</c>
/// comes before <c>/etc/hackeros/file-associations.json</c>, for the user an intent runs as only.
/// </summary>
public sealed class UserFileAssociationTests
{
    private const string Notepad = "org.hackeros.notepad";
    private const string TextEditor = "org.hackeros.texteditor";
    private const string Browser = "org.hackeros.browser";
    private const string OtherBrowser = "com.example.browser";
    private static readonly VirtualPath Notes = VirtualPath.Parse("/home/alice/notes.txt");

    [Fact]
    public async Task The_users_default_comes_before_the_systems()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.ConfigureSystemAsync(".txt", Notepad);

        Assert.Equal(UserDefaultHandlerChangeStatus.Changed, await fixture.Resolver.SetUserDefaultAsync(fixture.Alice, ".txt", "open", TextEditor));
        FileHandlerResolution resolution = await fixture.Resolver.ResolveAsync(new OpenFileIntent(Notes, FileIntentAction.Open), fixture.Alice);

        Assert.Equal(FileHandlerResolutionStatus.ConfiguredDefault, resolution.Status);
        Assert.Equal(TextEditor, resolution.AppId);
    }

    [Fact]
    public async Task A_call_that_names_no_user_reads_the_system_layer_only()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.ConfigureSystemAsync(".txt", Notepad);
        await fixture.Resolver.SetUserDefaultAsync(fixture.Alice, ".txt", "open", TextEditor);

        FileHandlerResolution resolution = await fixture.Resolver.ResolveAsync(new OpenFileIntent(Notes, FileIntentAction.Open));

        Assert.Equal(Notepad, resolution.AppId);
    }

    [Fact]
    public async Task A_users_choice_does_not_change_another_users_default()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.ConfigureSystemAsync(".txt", Notepad);
        await fixture.Resolver.SetUserDefaultAsync(fixture.Alice, ".txt", "open", TextEditor);
        await fixture.Harness.Session.LogoutAsync();
        AuthenticatedPrincipal bob = await fixture.LoginAsync(fixture.Harness.Bob);

        FileHandlerResolution resolution = await fixture.Resolver.ResolveAsync(
            new OpenFileIntent(VirtualPath.Parse("/home/bob/notes.txt"), FileIntentAction.Open), bob);

        Assert.Equal(Notepad, resolution.AppId);
    }

    [Fact]
    public async Task A_user_choice_without_a_system_default_replaces_the_chooser()
    {
        Fixture fixture = await Fixture.CreateAsync();
        Assert.Equal(FileHandlerResolutionStatus.ChooserRequired,
            (await fixture.Resolver.ResolveAsync(new OpenFileIntent(Notes, FileIntentAction.Open), fixture.Alice)).Status);

        await fixture.Resolver.SetUserDefaultAsync(fixture.Alice, ".txt", "open", Notepad);

        Assert.Equal(Notepad, (await fixture.Resolver.ResolveAsync(new OpenFileIntent(Notes, FileIntentAction.Open), fixture.Alice)).AppId);
    }

    [Fact]
    public async Task A_choice_naming_an_uninstalled_app_falls_back_to_the_system_default()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.ConfigureSystemAsync(".txt", Notepad);
        await fixture.Resolver.SetUserDefaultAsync(fixture.Alice, ".txt", "open", TextEditor);
        fixture.Disable(TextEditor);

        Assert.Equal(Notepad, (await fixture.Resolver.ResolveAsync(new OpenFileIntent(Notes, FileIntentAction.Open), fixture.Alice)).AppId);
    }

    [Fact]
    public async Task Open_and_edit_are_chosen_separately()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.Resolver.SetUserDefaultAsync(fixture.Alice, ".txt", "open", Notepad);
        await fixture.Resolver.SetUserDefaultAsync(fixture.Alice, ".txt", "edit", TextEditor);

        Assert.Equal(Notepad, (await fixture.Resolver.ResolveAsync(new OpenFileIntent(Notes, FileIntentAction.Open), fixture.Alice)).AppId);
        Assert.Equal(TextEditor, (await fixture.Resolver.ResolveAsync(new OpenFileIntent(Notes, FileIntentAction.Edit), fixture.Alice)).AppId);
    }

    [Fact]
    public async Task The_file_is_written_in_the_users_config_as_0644_and_a_reset_keeps_other_entries()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.Resolver.SetUserDefaultAsync(fixture.Alice, ".TXT", "open", Notepad);
        await fixture.Resolver.SetUserDefaultAsync(fixture.Alice, "x-scheme-handler/https", "open", OtherBrowser);

        FileSystemEntryMetadata? stat = await fixture.Harness.StatAsync(UserFile);
        Assert.NotNull(stat);
        Assert.Equal((ushort)0b110_100_100, stat!.Permissions.Mode);
        Assert.Equal(UserDefaultHandlerChangeStatus.Changed, await fixture.Resolver.SetUserDefaultAsync(fixture.Alice, ".txt", "open", null));

        string? content = await fixture.Harness.ReadAsync(UserFile);
        Assert.DoesNotContain(".txt", content!, StringComparison.Ordinal);
        Assert.Contains("\"x-scheme-handler/https\"", content!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invalid_user_file_is_ignored_and_never_overwritten()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.ConfigureSystemAsync(".txt", Notepad);
        await fixture.Harness.WriteAsUserAsync(UserFile, "{ not json");

        Assert.Equal(Notepad, (await fixture.Resolver.ResolveAsync(new OpenFileIntent(Notes, FileIntentAction.Open), fixture.Alice)).AppId);
        Assert.Equal(UserDefaultHandlerChangeStatus.FileInvalid, await fixture.Resolver.SetUserDefaultAsync(fixture.Alice, ".txt", "open", TextEditor));
        Assert.Equal("{ not json", await fixture.Harness.ReadAsync(UserFile));
    }

    [Theory]
    [InlineData("txt", "open", Notepad, UserDefaultHandlerChangeStatus.InvalidTarget)]
    [InlineData("text/plain/extra", "open", Notepad, UserDefaultHandlerChangeStatus.InvalidTarget)]
    [InlineData(".txt", "print", Notepad, UserDefaultHandlerChangeStatus.InvalidAction)]
    [InlineData(".txt", "open", "org.example.missing", UserDefaultHandlerChangeStatus.AppNotFound)]
    [InlineData(".png", "open", Notepad, UserDefaultHandlerChangeStatus.AppCannotHandle)]
    [InlineData("x-scheme-handler/https", "open", Notepad, UserDefaultHandlerChangeStatus.AppCannotHandle)]
    public async Task A_choice_the_os_could_not_use_is_refused(string target, string action, string appId, UserDefaultHandlerChangeStatus expected)
    {
        Fixture fixture = await Fixture.CreateAsync();

        Assert.Equal(expected, await fixture.Resolver.SetUserDefaultAsync(fixture.Alice, target, action, appId));
        Assert.Null(await fixture.Harness.StatAsync(UserFile));
    }

    [Fact]
    public async Task A_scheme_opens_with_the_users_browser()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.ConfigureSystemAsync(null, Browser, mediaType: "x-scheme-handler/https");
        await fixture.Resolver.SetUserDefaultAsync(fixture.Alice, "x-scheme-handler/https", "open", OtherBrowser);

        Assert.Equal(OtherBrowser, (await fixture.Resolver.ResolveSchemeAsync("https", fixture.Alice)).AppId);
        Assert.Equal(Browser, (await fixture.Resolver.ResolveSchemeAsync("https")).AppId);
    }

    [Fact]
    public async Task Open_with_lists_the_users_default_first()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.ConfigureSystemAsync(".txt", Notepad);
        await fixture.Resolver.SetUserDefaultAsync(fixture.Alice, ".txt", "open", TextEditor);

        FileHandlerCandidates candidates = await fixture.Resolver.GetHandlerAppIdsAsync(Notes, fixture.Alice);

        Assert.Equal(TextEditor, candidates.DefaultAppId);
        Assert.Equal([TextEditor, Notepad], candidates.AppIds);
    }

    [Fact]
    public async Task The_configured_defaults_list_the_users_rows_before_the_systems()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.ConfigureSystemAsync(".txt", Notepad);
        await fixture.Resolver.SetUserDefaultAsync(fixture.Alice, ".txt", "open", TextEditor);

        IReadOnlyList<ConfiguredFileAssociation> rows = await fixture.Resolver.GetConfiguredDefaultsAsync(fixture.Alice);

        Assert.Equal(new ConfiguredFileAssociation(".txt", "open", TextEditor, FileAssociationLayer.User), rows[0]);
        Assert.Contains(new ConfiguredFileAssociation(".txt", "open", Notepad, FileAssociationLayer.System), rows);
        Assert.Contains(new ConfiguredFileAssociation(".txt", "edit", Notepad, FileAssociationLayer.System), rows);
        Assert.DoesNotContain(rows, row => row.Layer == FileAssociationLayer.User && row.Action == "edit");
    }

    private const string UserFile = "/home/alice/.config/hackeros/file-associations.json";

    private static AppManifest Manifest(string id, params FileHandlerManifest[] handlers) => new()
    {
        Id = id,
        Name = id,
        Version = "1.0.0",
        PublisherId = "org.hackeros",
        Description = "An app for ADR 0121 tests.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", $"{id}.EntryPoint"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        FileHandlers = handlers
    };

    private sealed class Fixture
    {
        private readonly AppEnablementRegistry _enablement;

        private Fixture(SettingsFilesHarness harness)
        {
            Harness = harness;
            AppCatalogBuildResult catalogResult = AppCatalog.Build(
            [
                Manifest(Notepad, new FileHandlerManifest("text/plain", [".txt"], ["open", "edit"])),
                Manifest(TextEditor, new FileHandlerManifest("text/plain", [".txt"], ["open", "edit"])),
                Manifest(Browser, new FileHandlerManifest("x-scheme-handler/https", [], ["open"])),
                Manifest(OtherBrowser, new FileHandlerManifest("x-scheme-handler/https", [], ["open"]))
            ]);
            Assert.True(catalogResult.IsSuccess, string.Join(", ", catalogResult.Errors.Select(e => e.Message)));
            _enablement = new AppEnablementRegistry(catalogResult.Catalog!);
            Resolver = new FileAssociationResolver(catalogResult.Catalog!, _enablement, harness.Settings, harness.FileSystem);
        }

        internal SettingsFilesHarness Harness { get; }

        internal FileAssociationResolver Resolver { get; }

        internal AuthenticatedPrincipal Alice { get; private set; } = null!;

        internal static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new(new SettingsFilesHarness());
            await fixture.Harness.BootAsync();
            fixture.Alice = await fixture.LoginAsync(fixture.Harness.Alice);
            return fixture;
        }

        internal async Task<AuthenticatedPrincipal> LoginAsync(LocalUser user) => await Harness.LoginAsync(user);

        internal void Disable(string appId) => _enablement.MarkDisabled([appId]);

        internal Task ConfigureSystemAsync(string? extension, string appId, string? mediaType = null) =>
            Harness.WriteAsRootAsync(
                FileAssociationSettingsDocuments.File.Resolve(null).Value,
                extension is not null
                    ? $$"""{"schemaVersion":1,"associations":[{"extension":"{{extension}}","appId":"{{appId}}","actions":["open","edit"]}]}"""
                    : $$"""{"schemaVersion":1,"associations":[{"mediaType":"{{mediaType}}","appId":"{{appId}}","actions":["open"]}]}""");
    }
}
