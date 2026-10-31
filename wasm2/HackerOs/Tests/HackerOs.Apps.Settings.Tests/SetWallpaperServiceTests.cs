using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Settings;
using HackerOs.Platform.Core.Appearance;
using HackerOs.Platform.Core.ContextMenus;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Notifications;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Tests.Support;
using Xunit;

namespace HackerOs.Apps.Settings.Tests;

/// <summary>
/// « Set as wallpaper » (ADR 0218): the entries Settings registers in the image menus, and the hidden
/// service that writes the chosen image to <c>appearance.config</c> and says so in a notification.
/// </summary>
public sealed class SetWallpaperServiceTests
{
    private const string WebpHeader = "RIFF\u0010\0\0\0WEBPVP8 ";

    [Fact]
    public void The_manifest_is_a_hidden_service_with_a_plain_entry_and_a_submenu_of_screens()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new SetWallpaperApp());
        Assert.Equal(AppKind.Service, manifest.Kind);
        Assert.Equal(AppExecutionHost.MainThread, manifest.Execution);
        Assert.True(AppManifestValidator.Validate(manifest with { ContextMenus = Registered() }).IsValid);

        IReadOnlyList<AppContextMenuEntryManifest> entries = Registered();
        AppContextMenuEntryManifest single = entries.Single(static entry => entry.Name == SetWallpaperApp.SingleEntry);
        Assert.Equal(AppContextMenus.ScreensSingle, single.Screens);
        Assert.Equal(AppContextMenus.SelectionSingle, single.Selection);
        Assert.Contains(".webp", single.Types!);
        Assert.Equal(AppContextMenus.ScreensMultiple, entries.Single(static entry => entry.Name == SetWallpaperApp.SubmenuEntry).Screens);
        Assert.Equal(
            [null, 2, 3, 4, 5, 6, 7, 8, 9],
            entries.Where(static entry => entry.Parent == SetWallpaperApp.SubmenuEntry).Select(static entry => entry.Screen));
    }

    [Theory]
    [InlineData("set-wallpaper", 1)]
    [InlineData("set-wallpaper-all", 1)]
    [InlineData("set-wallpaper-screen-4", 4)]
    [InlineData("set-wallpaper-screen-1", 1)]
    [InlineData("set-wallpaper-screen-12", 1)]
    [InlineData(null, 1)]
    public void The_entry_name_gives_the_screen(string? entry, int screen)
    {
        Assert.Equal(screen, SetWallpaperService.ScreenOf(entry));
    }

    [Fact]
    public async Task One_click_writes_the_image_for_every_screen_and_posts_a_notification()
    {
        Harness harness = new(new FakeAppFileSystemGateway().WithFile("/home/alice/Pictures/beach.webp", WebpHeader));

        SetWallpaperService.Outcome outcome = await harness.ApplyAsync("/home/alice/Pictures/beach.webp", SetWallpaperApp.SingleEntry);

        Assert.Equal(SetWallpaperService.Outcome.Changed, outcome);
        Assert.Equal("/home/alice/Pictures/beach.webp", harness.Settings.User[AppearanceSettingsDocuments.WallpaperKey]);
        (NotificationSeverity severity, string title, string message) = Assert.Single(harness.Notifications.Posted);
        Assert.Equal(NotificationSeverity.Success, severity);
        Assert.Equal("Wallpaper changed", title);
        Assert.Equal("beach.webp is now the wallpaper.", message);
    }

    [Fact]
    public async Task The_entry_of_a_secondary_screen_writes_its_own_key()
    {
        Harness harness = new(new FakeAppFileSystemGateway().WithFile("/home/alice/city.webp", WebpHeader), lang: "fr_CA.UTF-8");

        await harness.ApplyAsync("/home/alice/city.webp", SetWallpaperApp.ScreenEntryPrefix + "3");

        Assert.Equal("/home/alice/city.webp", harness.Settings.User[AppearanceSettingsDocuments.WallpaperScreenKey(3)]);
        Assert.False(harness.Settings.User.ContainsKey(AppearanceSettingsDocuments.WallpaperKey));
        Assert.Equal("city.webp est maintenant le fond d'écran de l'écran 3.", Assert.Single(harness.Notifications.Posted).Message);
    }

    [Fact]
    public async Task A_file_that_is_not_an_image_or_cannot_be_read_changes_nothing()
    {
        Harness harness = new(new FakeAppFileSystemGateway().WithFile("/home/alice/fake.png", "<svg/>"));

        Assert.Equal(SetWallpaperService.Outcome.NotImage, await harness.ApplyAsync("/home/alice/fake.png", SetWallpaperApp.SingleEntry));
        Assert.Equal(SetWallpaperService.Outcome.Unreadable, await harness.ApplyAsync("/home/alice/missing.png", SetWallpaperApp.SingleEntry));

        Assert.Empty(harness.Settings.User);
        Assert.All(harness.Notifications.Posted, static posted => Assert.Equal(NotificationSeverity.Error, posted.Severity));
        Assert.Contains("is not a PNG, JPEG or WebP image", harness.Notifications.Posted[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_the_administrator_locked_is_reported()
    {
        Harness harness = new(new FakeAppFileSystemGateway().WithFile("/home/alice/beach.webp", WebpHeader));
        harness.Settings.Locked.Add(AppearanceSettingsDocuments.WallpaperKey);

        Assert.Equal(SetWallpaperService.Outcome.NotSaved, await harness.ApplyAsync("/home/alice/beach.webp", SetWallpaperApp.SingleEntry));
        Assert.Empty(harness.Settings.User);
        Assert.Equal(NotificationSeverity.Error, Assert.Single(harness.Notifications.Posted).Severity);
    }

    [Fact]
    public async Task An_intent_without_a_path_does_nothing()
    {
        Harness harness = new(new FakeAppFileSystemGateway());

        SetWallpaperService.Outcome outcome = await SetWallpaperService.ApplyAsync(
            harness.Context, new AppIntentDelivery(SetWallpaperApp.SetWallpaperIntent, """{"paths":[],"scope":"File"}"""), CancellationToken.None);

        Assert.Equal(SetWallpaperService.Outcome.NoPath, outcome);
        Assert.Empty(harness.Notifications.Posted);
    }

    private static IReadOnlyList<AppContextMenuEntryManifest> Registered()
    {
        HackerOs.AppSdk.Blazor.Files.RegisterFileContextMenuContext context = new();
        SetWallpaperApp.RegisterFileContextMenu(context);
        return [.. context.Entries.Select(static entry => entry with { Titles = [new(AppContextMenus.EnglishCulture, entry.Title)] })];
    }

    /// <summary>The service's context over in-memory files, settings and notifications.</summary>
    private sealed class Harness
    {
        public Harness(FakeAppFileSystemGateway files, string lang = "en_US.UTF-8")
        {
            FakeAppEnvironmentGateway environment = new("/home/alice", "alice");
            environment.SetVariable("LANG", lang);
            Context = new MinimalAppExecutionContext(
                AppDefinitionMapper.ToManifest(new SetWallpaperApp()),
                fileSystem: files,
                notifications: Notifications,
                userId: "alice",
                homeDirectory: "/home/alice",
                environment: environment,
                systemSettings: Settings);
        }

        public FakeAppearance Settings { get; } = new();

        public RecordingNotifications Notifications { get; } = new();

        public IAppExecutionContext Context { get; }

        /// <summary>Runs one click, with the payload the OS builds for the entry.</summary>
        public Task<SetWallpaperService.Outcome> ApplyAsync(string path, string entry) =>
            SetWallpaperService.ApplyAsync(
                Context,
                new AppIntentDelivery(SetWallpaperApp.SetWallpaperIntent, FileContextMenuLauncher.PayloadOf([path], "File", entry), FileContextMenuLauncher.ShellAppId),
                CancellationToken.None);
    }

    private sealed class RecordingNotifications : IAppNotificationGateway
    {
        public List<(NotificationSeverity Severity, string Title, string Message)> Posted { get; } = [];

        public NotificationId Post(NotificationSeverity severity, string title, string message, IReadOnlyList<NotificationAction>? actions = null, TimeSpan? expiresAfter = null)
        {
            Posted.Add((severity, title, message));
            return NotificationId.FromGuid(Guid.NewGuid());
        }
    }

    /// <summary>The OS settings gateway over one in-memory user layer, with the OS's schema of <c>appearance.config</c>.</summary>
    private sealed class FakeAppearance : IAppSystemSettingsGateway
    {
        public Dictionary<string, string> User { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Locked { get; } = new(StringComparer.Ordinal);

        public AppSystemSettingsDocumentInfo Describe(string documentId) =>
            documentId == AppearanceSettingsDocuments.Document.DocumentId
                ? new(documentId, AppearanceSettingsDocuments.Schema, "/etc/hackeros/appearance.config", "/home/alice/.config/hackeros/appearance.config", false)
                : throw new ArgumentException($"Unknown document '{documentId}'.", nameof(documentId));

        public ValueTask<LayeredConfigSnapshot> ReadAsync(string documentId, CancellationToken cancellationToken = default)
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            Dictionary<string, SettingLayer> sources = new(StringComparer.Ordinal);
            foreach (SettingFieldDeclaration field in AppearanceSettingsDocuments.Schema.Fields.Values)
            {
                bool set = User.TryGetValue(field.QualifiedKey, out string? value);
                values[field.QualifiedKey] = set ? value! : field.DefaultValue;
                sources[field.QualifiedKey] = set ? SettingLayer.User : SettingLayer.Default;
            }

            return ValueTask.FromResult(new LayeredConfigSnapshot(values, sources, new HashSet<string>(Locked), []));
        }

        public ValueTask SetAsync(string documentId, IReadOnlyList<KeyValuePair<string, string>> values, SettingLayer target, CancellationToken cancellationToken = default)
        {
            Assert.Equal(SettingLayer.User, target);
            foreach ((string key, string value) in values)
            {
                User[key] = value;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask ResetAsync(string documentId, string qualifiedKey, SettingLayer target, CancellationToken cancellationToken = default)
        {
            User.Remove(qualifiedKey);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IAsyncDisposable> WatchAsync(string documentId, Func<ValueTask> onChanged, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IAsyncDisposable>(new NoWatch());
    }

    private sealed class NoWatch : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
