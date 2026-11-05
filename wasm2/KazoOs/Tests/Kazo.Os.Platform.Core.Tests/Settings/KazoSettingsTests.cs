using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Settings;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Tests.Settings;

/// <summary>
/// Contract of the SDK's <see cref="KazoSettings"/> over the real filesystem: settings are files,
/// permissions decide, layering and locks follow ADR 0044.
/// </summary>
public sealed class KazoSettingsTests
{
    private const string AppId = "org.example.editor";
    private const string UserFile = "/home/alice/.config/org.example.editor/settings.config";
    private const string SystemFile = "/etc/org.example.editor/settings.config";

    private enum EditorTheme
    {
        Dark,
        Light,
        HighContrast
    }

    [Fact]
    public async Task Defaults_apply_when_no_file_exists_and_nothing_is_created_by_reading()
    {
        (SettingsFilesHarness harness, TestAppContext app) = await CreateAsync();

        Assert.Equal(EditorTheme.Dark, await KazoSettings.GetAsync<EditorTheme>(app, "theme"));
        Assert.Equal(4, await KazoSettings.GetAsync<int>(app, "Editor.tab-size"));
        Assert.Null(await harness.StatAsync(UserFile));
    }

    [Fact]
    public async Task Set_writes_a_plain_text_user_file_that_keeps_what_a_human_wrote()
    {
        (SettingsFilesHarness harness, TestAppContext app) = await CreateAsync();
        await harness.WriteAsUserAsync(UserFile, "# tuned by hand\ntheme=dark\n");

        await KazoSettings.SetAsync(app, "theme", EditorTheme.HighContrast);
        await KazoSettings.SetAsync(app, "Editor.tab-size", 2);

        Assert.Equal("# tuned by hand\ntheme=high-contrast\n\n[Editor]\ntab-size=2\n", await harness.ReadAsync(UserFile));
        Assert.Equal(EditorTheme.HighContrast, await KazoSettings.GetAsync<EditorTheme>(app, "theme"));
    }

    [Fact]
    public async Task New_user_file_is_owned_by_the_user_with_regular_permissions()
    {
        (SettingsFilesHarness harness, TestAppContext app) = await CreateAsync();

        await KazoSettings.SetAsync(app, "autosave", false);

        var metadata = await harness.StatAsync(UserFile);
        Assert.NotNull(metadata);
        Assert.Contains(metadata.OwnerId, new[] { "alice", harness.Alice.Id.ToString() });
        Assert.Equal(0x01A4, metadata.Permissions.Mode);
    }

    [Fact]
    public async Task System_layer_is_the_default_for_every_user_and_lock_wins()
    {
        (SettingsFilesHarness harness, TestAppContext app) = await CreateAsync();
        await harness.WriteAsRootAsync(SystemFile, "theme=light\nautosave=false\n[locked]\nautosave=true\n");
        await harness.WriteAsUserAsync(UserFile, "autosave=true\n");

        await using KazoSettingsDocument document = await KazoSettings.OpenAsync(app);

        Assert.Equal(EditorTheme.Light, document.Get<EditorTheme>("theme"));
        Assert.Equal(SettingLayer.System, document.GetSource("theme"));
        Assert.False(document.Get<bool>("autosave"));
        Assert.True(document.IsLocked("autosave"));
        KazoSettingLockedException locked = await Assert.ThrowsAsync<KazoSettingLockedException>(
            () => document.SetAsync("autosave", true).AsTask());
        Assert.Equal(SystemFile, locked.SystemPath);
    }

    [Fact]
    public async Task Reset_removes_the_user_value_so_the_system_default_applies_again()
    {
        (SettingsFilesHarness harness, TestAppContext app) = await CreateAsync();
        await harness.WriteAsRootAsync(SystemFile, "theme=light\n");
        await using KazoSettingsDocument document = await KazoSettings.OpenAsync(app);
        await document.SetAsync("theme", EditorTheme.Dark);
        Assert.Equal(SettingLayer.User, document.GetSource("theme"));

        await document.ResetAsync("theme");

        Assert.Equal(EditorTheme.Light, document.Get<EditorTheme>("theme"));
        Assert.Equal(SettingLayer.System, document.GetSource("theme"));
    }

    [Fact]
    public async Task Bad_lines_are_ignored_and_logged_once_per_file_revision()
    {
        (SettingsFilesHarness harness, TestAppContext app) = await CreateAsync();
        await harness.WriteAsUserAsync(UserFile, "theme=purple\nwhat is this\n");

        await using KazoSettingsDocument document = await KazoSettings.OpenAsync(app);
        await document.RefreshAsync();

        Assert.Equal(EditorTheme.Dark, document.Get<EditorTheme>("theme"));
        Assert.Equal(
            [
                $"settings: {UserFile}:1: invalid value for 'theme' (expected one of: dark, light, high-contrast), using default",
                $"settings: {UserFile}:2: malformed line ignored"
            ],
            app.LoggedWarnings);
    }

    [Fact]
    public async Task Writing_the_system_layer_needs_the_file_permissions_not_just_a_capability()
    {
        (SettingsFilesHarness harness, TestAppContext app) = await CreateAsync(AppCapabilities.FileSystemSystemWrite);
        await harness.WriteAsRootAsync(SystemFile, "theme=light\n");
        await using KazoSettingsDocument document = await KazoSettings.OpenAsync(app);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => document.SetAsync("theme", EditorTheme.Dark, SettingLayer.System).AsTask());
        Assert.Equal("theme=light\n", await harness.ReadAsync(SystemFile));
    }

    [Fact]
    public async Task Another_app_cannot_touch_this_apps_private_settings_directory()
    {
        (SettingsFilesHarness harness, TestAppContext app) = await CreateAsync();
        await KazoSettings.SetAsync(app, "autosave", false);
        AuthenticatedPrincipal alice = harness.Session.CurrentPrincipal!;
        IAppFileSystemGateway otherApp = harness.CreateAppGateway(
            alice, "org.example.other", AppCapabilities.FileSystemPrivateRead, AppCapabilities.FileSystemPrivateWrite);

        SettingsFileReadResult read = await SettingsTextFile.ReadAsync(otherApp, VirtualPath.Parse(UserFile));

        Assert.Null(read.Content);
        Assert.NotNull(read.Error);
    }

    [Fact]
    public async Task External_edit_raises_changed_with_the_changed_keys()
    {
        (SettingsFilesHarness harness, TestAppContext app) = await CreateAsync();
        await KazoSettings.SetAsync(app, "autosave", false);
        await using KazoSettingsDocument document = await KazoSettings.OpenAsync(app);
        TaskCompletionSource<IReadOnlyList<string>> changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        document.Changed += (_, args) => changed.TrySetResult(args.ChangedKeys);

        await harness.WriteAsUserAsync(UserFile, "autosave=false\ntheme=light\n");

        IReadOnlyList<string> keys = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["theme"], keys);
        Assert.Equal(EditorTheme.Light, document.Get<EditorTheme>("theme"));
    }

    [Fact]
    public async Task Ambient_context_overloads_act_as_the_ambient_app()
    {
        (SettingsFilesHarness harness, TestAppContext app) = await CreateAsync();

        using (KazoExecutionContext.Enter(app))
        {
            await KazoSettings.SetAsync("Editor.tab-size", 8);
            Assert.Equal(8, await KazoSettings.GetAsync<int>("Editor.tab-size"));
        }

        Assert.Equal("[Editor]\ntab-size=8\n", await harness.ReadAsync(UserFile));
        await Assert.ThrowsAsync<KazoExecutionContextMissingException>(() => KazoSettings.GetAsync<int>("Editor.tab-size").AsTask());
    }

    [Fact]
    public async Task Undeclared_key_or_wrong_type_is_a_programming_error()
    {
        (_, TestAppContext app) = await CreateAsync();
        await using KazoSettingsDocument document = await KazoSettings.OpenAsync(app);

        Assert.Throws<ArgumentException>(() => document.Get<string>("font"));
        await Assert.ThrowsAsync<ArgumentException>(() => document.SetAsync("Editor.tab-size", "wide").AsTask());
    }

    private static async Task<(SettingsFilesHarness Harness, TestAppContext App)> CreateAsync(params string[] extraCapabilities)
    {
        SettingsFilesHarness harness = new();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        string[] capabilities = [AppCapabilities.FileSystemPrivateRead, AppCapabilities.FileSystemPrivateWrite, .. extraCapabilities];
        return (harness, new TestAppContext(harness, alice, Manifest, capabilities));
    }

    private static AppManifest Manifest { get; } = new()
    {
        Id = AppId,
        Name = "Editor",
        Version = "1.0.0",
        PublisherId = "org.example",
        Description = "Settings contract test app.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", "Editor"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Settings = new AppSettingsSchemaManifest(
            1,
            [
                new AppSettingFieldManifest("theme", AppSettingValueType.Enum, "dark", AppSettingScope.AppUser, AppSettingSensitivity.Public, ["dark", "light", "high-contrast"]),
                new AppSettingFieldManifest("autosave", AppSettingValueType.Boolean, "true", AppSettingScope.AppUser, AppSettingSensitivity.Public),
                new AppSettingFieldManifest("tab-size", AppSettingValueType.Integer, "4", AppSettingScope.AppUser, AppSettingSensitivity.Public, Group: "Editor")
            ])
    };

    /// <summary>Execution context wired to the real filesystem, watch and a capturing log.</summary>
    private sealed class TestAppContext : IAppExecutionContext, IAppLoggingGateway
    {
        private readonly AppOperationContext _operation;

        internal TestAppContext(SettingsFilesHarness harness, AuthenticatedPrincipal principal, AppManifest manifest, string[] capabilities)
        {
            Manifest = manifest;
            UserId = principal.UserId.ToString();
            HomeDirectory = principal.HomePath;
            _operation = new AppOperationContext
            {
                AppId = manifest.Id,
                UserId = UserId,
                UserName = principal.LoginName.Value,
                UserAuthority = principal.Authority,
                GrantedCapabilities = new HashSet<string>(capabilities, StringComparer.Ordinal)
            };
            string[] groups = [.. principal.GroupIds.Select(group => group.ToString())];
            FileSystem = new AppFileSystemGateway(harness.FileSystem, harness.Clock, _operation, groups);
            Watch = new AppFileSystemWatchGateway(harness.FileSystem, harness.TopicBus, harness.Clock, _operation, groups, "1");
            GrantedCapabilities = _operation.GrantedCapabilities;
        }

        public List<string> LoggedWarnings { get; } = [];
        public AppManifest Manifest { get; }
        public Guid InstanceId { get; } = Guid.NewGuid();
        public string UserId { get; }
        public string HomeDirectory { get; }
        public AppAuthority UserAuthority => AppAuthority.User;
        public IReadOnlySet<string> GrantedCapabilities { get; }
        public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());
        public ProcessId ProcessId { get; } = ProcessId.FromInt64(1);
        public CancellationToken CancellationToken => CancellationToken.None;
        public IAppFileSystemGateway FileSystem { get; }
        public IAppFileSystemWatchGateway Watch { get; }
        public IAppLoggingGateway Logging => this;
        public ICapabilityChecker Capabilities => throw new NotSupportedException();
        public IAppEventGateway Events => throw new NotSupportedException();
        public IAppNotificationGateway Notifications => throw new NotSupportedException();
        public IAppClockGateway Clock => throw new NotSupportedException();
        public IAppProcessGateway Processes => throw new NotSupportedException();

        public void Log(DiagnosticSeverity severity, string message, IReadOnlyDictionary<string, string>? properties = null)
        {
            if (severity == DiagnosticSeverity.Warning)
            {
                LoggedWarnings.Add(message);
            }
        }
    }
}
