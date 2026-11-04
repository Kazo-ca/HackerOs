using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Intents;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Simulation.Abstractions;

namespace Kazo.Os.Platform.Core.Tests.Intents;

/// <summary>
/// Tests for `P1-APP-008`, `P1-APP-008A`, `P1-APP-008B`, and `P1-APP-009`: resolving an
/// <see cref="OpenFileIntent"/> to a target app using the documented precedence order.
/// </summary>
public sealed class FileAssociationResolverTests
{
    [Fact]
    public async Task An_explicit_valid_preferred_app_is_used_as_the_target()
    {
        Fixture fixture = new(NotepadManifest());
        OpenFileIntent intent = new(VirtualPath.Parse("/home/alice/notes.txt"), FileIntentAction.Open, PreferredAppId: "org.kazoos.notepad");

        FileHandlerResolution resolution = await fixture.Resolver.ResolveAsync(intent);

        Assert.Equal(FileHandlerResolutionStatus.ExplicitTarget, resolution.Status);
        Assert.Equal("org.kazoos.notepad", resolution.AppId);
    }

    [Fact]
    public async Task An_explicit_preferred_app_that_cannot_handle_the_file_is_invalid()
    {
        Fixture fixture = new(NotepadManifest());
        OpenFileIntent intent = new(VirtualPath.Parse("/home/alice/photo.png"), FileIntentAction.Open, PreferredAppId: "org.kazoos.notepad");

        FileHandlerResolution resolution = await fixture.Resolver.ResolveAsync(intent);

        Assert.Equal(FileHandlerResolutionStatus.TargetInvalid, resolution.Status);
    }

    [Fact]
    public async Task An_explicit_preferred_app_that_is_disabled_is_invalid()
    {
        Fixture fixture = new(NotepadManifest());
        fixture.Disable("org.kazoos.notepad");
        OpenFileIntent intent = new(VirtualPath.Parse("/home/alice/notes.txt"), FileIntentAction.Open, PreferredAppId: "org.kazoos.notepad");

        FileHandlerResolution resolution = await fixture.Resolver.ResolveAsync(intent);

        Assert.Equal(FileHandlerResolutionStatus.TargetInvalid, resolution.Status);
    }

    [Fact]
    public async Task A_configured_default_from_the_association_document_is_preferred_over_candidates()
    {
        Fixture fixture = new(NotepadManifest(), TextEditorManifest());
        await fixture.ConfigureDefaultAsync(".txt", "org.kazoos.texteditor");
        OpenFileIntent intent = new(VirtualPath.Parse("/home/alice/notes.txt"), FileIntentAction.Open);

        FileHandlerResolution resolution = await fixture.Resolver.ResolveAsync(intent);

        Assert.Equal(FileHandlerResolutionStatus.ConfiguredDefault, resolution.Status);
        Assert.Equal("org.kazoos.texteditor", resolution.AppId);
    }

    [Fact]
    public async Task A_sole_manifest_declared_candidate_is_used_without_a_configured_default()
    {
        Fixture fixture = new(NotepadManifest());
        OpenFileIntent intent = new(VirtualPath.Parse("/home/alice/notes.txt"), FileIntentAction.Open);

        FileHandlerResolution resolution = await fixture.Resolver.ResolveAsync(intent);

        Assert.Equal(FileHandlerResolutionStatus.SoleCandidate, resolution.Status);
        Assert.Equal("org.kazoos.notepad", resolution.AppId);
    }

    [Fact]
    public async Task Multiple_candidates_without_a_configured_default_require_a_chooser()
    {
        Fixture fixture = new(NotepadManifest(), TextEditorManifest());
        OpenFileIntent intent = new(VirtualPath.Parse("/home/alice/notes.txt"), FileIntentAction.Open);

        FileHandlerResolution resolution = await fixture.Resolver.ResolveAsync(intent);

        Assert.Equal(FileHandlerResolutionStatus.ChooserRequired, resolution.Status);
        Assert.Equal(["org.kazoos.notepad", "org.kazoos.texteditor"], resolution.CandidateAppIds);
    }

    [Fact]
    public async Task No_enabled_candidate_returns_no_handler()
    {
        Fixture fixture = new(NotepadManifest());
        OpenFileIntent intent = new(VirtualPath.Parse("/home/alice/photo.png"), FileIntentAction.Open);

        FileHandlerResolution resolution = await fixture.Resolver.ResolveAsync(intent);

        Assert.Equal(FileHandlerResolutionStatus.NoHandler, resolution.Status);
    }

    // `INT-007`: a directory-only `FileHandlerManifest` (media type set, no extensions — a directory path
    // has none for `GetExtension` to find anyway) needs no resolver code change, since `MatchesTarget`
    // already falls through to the media-type branch whenever `extension` is null. These four tests prove
    // that holds across every outcome `MatchesTarget`/`TryGetDefault` can produce, using the real
    // `inode/directory` convention `FV-009`'s `FileView.ActivateItemAsync` already sends.

    [Fact]
    public async Task An_explicit_valid_preferred_app_is_used_for_a_directory_target()
    {
        Fixture fixture = new(DirectoryHandlerManifest("org.kazoos.file-explorer"));
        OpenFileIntent intent = new(
            VirtualPath.Parse("/home/alice/Documents"), FileIntentAction.Open,
            PreferredAppId: "org.kazoos.file-explorer", MediaType: "inode/directory");

        FileHandlerResolution resolution = await fixture.Resolver.ResolveAsync(intent);

        Assert.Equal(FileHandlerResolutionStatus.ExplicitTarget, resolution.Status);
        Assert.Equal("org.kazoos.file-explorer", resolution.AppId);
    }

    [Fact]
    public async Task A_configured_media_type_default_is_preferred_over_directory_candidates()
    {
        Fixture fixture = new(DirectoryHandlerManifest("org.kazoos.file-explorer"), DirectoryHandlerManifest("org.kazoos.archiver"));
        await fixture.ConfigureMediaTypeDefaultAsync("inode/directory", "org.kazoos.file-explorer");
        OpenFileIntent intent = new(VirtualPath.Parse("/home/alice/Documents"), FileIntentAction.Open, MediaType: "inode/directory");

        FileHandlerResolution resolution = await fixture.Resolver.ResolveAsync(intent);

        Assert.Equal(FileHandlerResolutionStatus.ConfiguredDefault, resolution.Status);
        Assert.Equal("org.kazoos.file-explorer", resolution.AppId);
    }

    [Fact]
    public async Task A_sole_directory_candidate_is_used_without_a_configured_default()
    {
        // FileAssociationSettingsDocuments.DefaultContent seeds org.kazoos.file-explorer as the
        // inode/directory default (INT-009) — cleared here so this test genuinely exercises the
        // no-configured-default candidate path, not the configured-default path INT-009 itself covers.
        Fixture fixture = new(DirectoryHandlerManifest("org.kazoos.file-explorer"));
        await fixture.ClearAssociationsAsync();
        OpenFileIntent intent = new(VirtualPath.Parse("/home/alice/Documents"), FileIntentAction.Open, MediaType: "inode/directory");

        FileHandlerResolution resolution = await fixture.Resolver.ResolveAsync(intent);

        Assert.Equal(FileHandlerResolutionStatus.SoleCandidate, resolution.Status);
        Assert.Equal("org.kazoos.file-explorer", resolution.AppId);
    }

    [Fact]
    public async Task Multiple_directory_candidates_without_a_configured_default_require_a_chooser()
    {
        Fixture fixture = new(DirectoryHandlerManifest("org.kazoos.file-explorer"), DirectoryHandlerManifest("org.kazoos.archiver"));
        await fixture.ClearAssociationsAsync();
        OpenFileIntent intent = new(VirtualPath.Parse("/home/alice/Documents"), FileIntentAction.Open, MediaType: "inode/directory");

        FileHandlerResolution resolution = await fixture.Resolver.ResolveAsync(intent);

        Assert.Equal(FileHandlerResolutionStatus.ChooserRequired, resolution.Status);
        Assert.Equal(["org.kazoos.archiver", "org.kazoos.file-explorer"], resolution.CandidateAppIds);
    }

    private static AppManifest DirectoryHandlerManifest(string appId) => new()
    {
        Id = appId,
        Name = appId,
        Version = "1.0.0",
        PublisherId = "org.kazoos",
        Description = "A directory-capable app for INT-007 tests.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", $"{appId}.EntryPoint"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        FileHandlers = [new FileHandlerManifest("inode/directory", [], ["open"])]
    };

    private static AppManifest NotepadManifest() => new()
    {
        Id = "org.kazoos.notepad",
        Name = "Notepad",
        Version = "1.0.0",
        PublisherId = "org.kazoos",
        Description = "A simple text editor.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", "org.kazoos.notepad.EntryPoint"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        FileHandlers = [new FileHandlerManifest("text/plain", [".txt"], ["open", "edit"])]
    };

    private static AppManifest TextEditorManifest() => new()
    {
        Id = "org.kazoos.texteditor",
        Name = "Text Editor",
        Version = "1.0.0",
        PublisherId = "org.kazoos",
        Description = "An advanced text editor.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", "org.kazoos.texteditor.EntryPoint"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        FileHandlers = [new FileHandlerManifest("text/plain", [".txt"], ["open", "edit"])]
    };

    private sealed class Fixture
    {
        private readonly AppEnablementRegistry _enablement;
        private readonly SettingsFilesHarness _harness = new();

        internal Fixture(params AppManifest[] manifests)
        {
            AppCatalogBuildResult catalogResult = AppCatalog.Build(manifests);
            Assert.True(catalogResult.IsSuccess, string.Join(", ", catalogResult.Errors.Select(e => e.Message)));
            AppCatalog catalog = catalogResult.Catalog!;
            _enablement = new AppEnablementRegistry(catalog);
            Resolver = new FileAssociationResolver(catalog, _enablement, _harness.Settings);
        }

        internal FileAssociationResolver Resolver { get; }

        internal void Disable(string appId) => _enablement.MarkDisabled([appId]);

        // The associations are the administrator file /etc/kazoos/file-associations.json (ADR 0044).
        internal Task ConfigureDefaultAsync(string extension, string appId) => WriteAsync($$"""
            {"schemaVersion":1,"associations":[{"extension":"{{extension}}","appId":"{{appId}}","actions":["open","edit"]}]}
            """);

        internal Task ConfigureMediaTypeDefaultAsync(string mediaType, string appId) => WriteAsync($$"""
            {"schemaVersion":1,"associations":[{"mediaType":"{{mediaType}}","appId":"{{appId}}","actions":["open"]}]}
            """);

        internal Task ClearAssociationsAsync() => WriteAsync("{\"schemaVersion\":1,\"associations\":[]}");

        private Task WriteAsync(string content) =>
            _harness.WriteAsRootAsync(FileAssociationSettingsDocuments.File.Resolve(null).Value, content);
    }
}
