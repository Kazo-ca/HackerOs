using System.Globalization;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk.WhatsNew;
using HackerOs.Platform.Core.Notifications;
using HackerOs.Platform.Core.Settings;
using HackerOs.Platform.Core.WhatsNew;
using HackerOs.Simulation.Abstractions;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Notifications;
using HackerOs.Simulation.Abstractions.Sessions;
using HackerOs.Simulation.Abstractions.Settings;

namespace HackerOs.Platform.Core.Tests.WhatsNew;

/// <summary>ADR 0186 §4, §5: the served change list, the ids each account has seen, and the new-account rule.</summary>
public sealed class WhatsNewServiceTests
{
    private const string Next = "11111111-1111-4111-8111-111111111111";
    private const string Latest = "22222222-2222-4222-8222-222222222222";
    private const string Older = "33333333-3333-4333-8333-333333333333";
    private const string Oldest = "44444444-4444-4444-8444-444444444444";

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-CA");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static readonly string Document = $$"""
        {
          "product": "HackerOS",
          "version": "2.2.0",
          "groups": [
            { "version": null, "since": "2.2.0", "entries": [ {{Entry(Next, "fixed")}} ] },
            { "version": "2.2.0", "date": "2026-11-02", "entries": [ {{Entry(Latest, "new")}} ] },
            { "version": "2.1.0", "date": "2026-10-26", "entries": [ {{Entry(Older, "improved")}}, {{Entry(Oldest, "developer")}} ] }
          ]
        }
        """;

    [Fact]
    public async Task A_new_account_sees_the_latest_version_and_the_next_one_as_new()
    {
        FakeSettings settings = new();
        WhatsNewService service = new(new FakeSource(Document), settings);

        HackChangeList list = await service.GetAsync(English);

        Assert.Equal(2, list.NewCount);
        Assert.Equal([Next, Latest], NewIds(list));
        // The rule is written at once: the next visit reads the same state.
        Assert.Equal([Older, Oldest], WhatsNewService.ParseState(settings.Files["whats-new.json"])!.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Marked_entries_lose_their_badge_and_stay_seen_in_the_state_file()
    {
        FakeSettings settings = new();
        WhatsNewService service = new(new FakeSource(Document), settings);
        int changes = 0;
        service.Changed += () => changes++;

        await service.MarkSeenAsync([Latest]);

        Assert.Equal(1, changes);
        Assert.Equal(1, await service.GetNewCountAsync());
        WhatsNewService reloaded = new(new FakeSource(Document), settings);
        Assert.Equal([Next], NewIds(await reloaded.GetAsync(English)));
    }

    [Fact]
    public async Task Marking_seen_ids_again_changes_nothing()
    {
        FakeSettings settings = new();
        WhatsNewService service = new(new FakeSource(Document), settings);
        await service.MarkSeenAsync([Latest]);
        int writes = settings.Writes;
        int changes = 0;
        service.Changed += () => changes++;

        await service.MarkSeenAsync([Latest, Older]);

        Assert.Equal(0, changes);
        Assert.Equal(writes, settings.Writes);
    }

    [Fact]
    public async Task An_existing_state_file_wins_over_the_new_account_rule()
    {
        FakeSettings settings = new();
        settings.Files["whats-new.json"] = WhatsNewService.SerializeState([Latest]);
        WhatsNewService service = new(new FakeSource(Document), settings);

        Assert.Equal([Next, Older, Oldest], NewIds(await service.GetAsync(English)));
    }

    [Fact]
    public async Task Without_a_served_list_nothing_is_new_and_nothing_is_written()
    {
        FakeSettings settings = new();
        WhatsNewService service = new(new FakeSource(null), settings);

        HackChangeList list = await service.GetAsync(English);

        Assert.Empty(list.Groups);
        Assert.Equal(0, list.NewCount);
        Assert.Empty(settings.Files);
    }

    [Fact]
    public async Task An_unreachable_list_is_read_again_at_the_next_call()
    {
        FakeSource source = new(null);
        WhatsNewService service = new(source, new FakeSettings());
        Assert.Equal(0, await service.GetNewCountAsync());

        source.Document = Document;

        Assert.Equal(2, await service.GetNewCountAsync());
    }

    [Fact]
    public async Task Without_a_session_nothing_is_new()
    {
        FakeSettings settings = new() { Home = null };
        WhatsNewService service = new(new FakeSource(Document), settings);

        Assert.Equal(0, await service.GetNewCountAsync());
        Assert.Empty(settings.Files);
    }

    [Fact]
    public async Task Each_account_has_its_own_state()
    {
        FakeSettings settings = new();
        WhatsNewService service = new(new FakeSource(Document), settings);
        await service.MarkSeenAsync([Next, Latest]);
        Assert.Equal(0, await service.GetNewCountAsync());

        settings.Home = "/home/bob";

        Assert.Equal(2, await service.GetNewCountAsync());
    }

    [Fact]
    public async Task Texts_follow_the_language_with_an_English_fallback()
    {
        WhatsNewService service = new(new FakeSource(Document), new FakeSettings());

        HackChangeList french = await service.GetAsync(French);
        HackChangeList german = await service.GetAsync(CultureInfo.GetCultureInfo("de-DE"));

        Assert.Equal("Titre " + Next, french.Groups[0].Entries[0].Title);
        Assert.Equal("Title " + Next, german.Groups[0].Entries[0].Title);
        Assert.Null(french.Groups[0].Entries[0].Description);
    }

    [Fact]
    public void A_malformed_state_file_counts_as_missing()
    {
        Assert.Null(WhatsNewService.ParseState("not json"));
        Assert.Null(WhatsNewService.ParseState("{ \"other\": [] }"));
        Assert.Equal([Latest], WhatsNewService.ParseState("{ \"seen\": [\"" + Latest + "\", 3, \"\"] }"));
    }

    [Fact]
    public void The_catalog_keeps_groups_entries_and_https_links_only()
    {
        WhatsNewCatalog catalog = WhatsNewCatalog.Parse("""
            {
              "groups": [
                { "version": null, "since": "2.1.0", "entries": [
                  { "id": "a", "category": "new", "title": { "en": "A" }, "prUrl": "https://github.com/Kazo-ca/HackerOs/pull/1",
                    "links": ["https://example.com/doc", "javascript:alert(1)", "http://example.com"] },
                  { "id": "b", "category": "unknown", "title": { "en": "B" } },
                  { "id": "c", "category": "fixed", "title": {} }
                ] },
                { "version": "2.1.0", "date": "2026-10-26", "entries": [ { "id": "a", "category": "new", "title": { "en": "dup" } } ] }
              ]
            }
            """);

        WhatsNewCatalogGroup group = Assert.Single(catalog.Groups);
        WhatsNewCatalogEntry entry = Assert.Single(group.Entries);
        Assert.Equal("2.1.0", group.Since);
        Assert.Equal(new Uri("https://github.com/Kazo-ca/HackerOs/pull/1"), entry.PullRequestUrl);
        Assert.Equal([new Uri("https://example.com/doc")], entry.Links);
    }

    [Fact]
    public async Task The_sign_in_notification_counts_the_new_changes_and_opens_the_Settings_page_once()
    {
        InMemoryNotificationQueue queue = new(maxEntriesPerUser: 10);
        AuthenticatedPrincipal alice = Principal("alice");
        WhatsNewService service = new(new FakeSource(Document), new FakeSettings(), notifications: queue, currentUser: () => alice);

        Assert.True(await service.NotifyNewChangesAsync());
        Assert.False(await service.NotifyNewChangesAsync());

        Notification notification = Assert.Single(queue.GetActive(alice.UserId, DateTimeOffset.UtcNow));
        Assert.Equal(WhatsNewService.NotificationSourceAppId, notification.SourceAppId);
        Assert.Equal("What's new", notification.Title);
        Assert.Equal("2 new changes since your last visit.", notification.Message);
        NotificationAction action = Assert.Single(notification.Actions);
        Assert.Equal("View", action.Label);
        Assert.True(action.TryGetLaunchedApp(out string? appId));
        Assert.Equal("org.hackeros.settings", appId);
        Assert.Equal(["--page=whats-new"], action.LaunchArguments);
    }

    [Fact]
    public async Task Seeing_every_change_dismisses_the_sign_in_notification_and_seeing_some_keeps_it()
    {
        InMemoryNotificationQueue queue = new(maxEntriesPerUser: 10);
        AuthenticatedPrincipal alice = Principal("alice");
        FakeSettings settings = new();
        settings.Files["whats-new.json"] = WhatsNewService.SerializeState([Older, Oldest]);
        WhatsNewService service = new(new FakeSource(Document), settings, notifications: queue, currentUser: () => alice);
        Assert.True(await service.NotifyNewChangesAsync());

        await service.MarkSeenAsync([Next]);
        Assert.Single(queue.GetActive(alice.UserId, DateTimeOffset.UtcNow));

        await service.MarkSeenAsync([Latest]);
        Assert.Empty(queue.GetActive(alice.UserId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task The_sign_in_notification_speaks_the_session_language()
    {
        InMemoryNotificationQueue queue = new(maxEntriesPerUser: 10);
        AuthenticatedPrincipal alice = Principal("alice");
        FakeSettings settings = new();
        settings.Files["whats-new.json"] = WhatsNewService.SerializeState([Next, Latest, Older]);
        WhatsNewService service = new(new FakeSource(Document), settings, notifications: queue, currentUser: () => alice, language: () => French);

        await service.NotifyNewChangesAsync();

        Notification notification = Assert.Single(queue.GetActive(alice.UserId, DateTimeOffset.UtcNow));
        Assert.Equal("Nouveautés", notification.Title);
        Assert.Equal("1 nouveauté depuis votre dernière visite.", notification.Message);
        Assert.Equal("Voir", notification.Actions[0].Label);
    }

    [Fact]
    public async Task Nothing_new_posts_no_notification()
    {
        InMemoryNotificationQueue queue = new(maxEntriesPerUser: 10);
        AuthenticatedPrincipal alice = Principal("alice");
        FakeSettings settings = new();
        settings.Files["whats-new.json"] = WhatsNewService.SerializeState([Next, Latest, Older, Oldest]);
        WhatsNewService service = new(new FakeSource(Document), settings, notifications: queue, currentUser: () => alice);

        Assert.False(await service.NotifyNewChangesAsync());
        Assert.Empty(queue.GetActive(alice.UserId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_launch_action_carries_its_arguments_and_an_old_one_has_none()
    {
        NotificationAction withArguments = NotificationAction.LaunchApp("org.hackeros.settings", "View", ["--page=whats-new", "--x"]);
        NotificationAction plain = NotificationAction.LaunchApp("org.hackeros.diagnostic", "Open");

        Assert.True(withArguments.TryGetLaunchedApp(out string? appId));
        Assert.Equal("org.hackeros.settings", appId);
        Assert.Equal(["--page=whats-new", "--x"], withArguments.LaunchArguments);
        Assert.True(plain.TryGetLaunchedApp(out appId));
        Assert.Equal("org.hackeros.diagnostic", appId);
        Assert.Empty(plain.LaunchArguments);
        Assert.Empty(new NotificationAction("reply", "Reply").LaunchArguments);
        Assert.Throws<ArgumentException>(() => NotificationAction.LaunchApp("a", "b", ["x\u001Fy"]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{ broken")]
    public void A_missing_or_malformed_document_is_an_empty_list(string? document) =>
        Assert.Empty(WhatsNewCatalog.Parse(document).Groups);

    private static AuthenticatedPrincipal Principal(string login)
    {
        LocalGroupId group = LocalGroupId.FromGuid(Guid.NewGuid());
        return new AuthenticatedPrincipal(
            SessionId.FromGuid(Guid.NewGuid()),
            LocalUserId.FromGuid(Guid.NewGuid()),
            LocalLoginName.Parse(login),
            login,
            AppAuthority.User,
            group,
            [group],
            InstallationId.FromGuid(Guid.NewGuid()),
            DeviceId.FromGuid(Guid.NewGuid()),
            DateTimeOffset.UtcNow);
    }

    private static string Entry(string id, string category) =>
        $$"""{ "id": "{{id}}", "category": "{{category}}", "title": { "en": "Title {{id}}", "fr": "Titre {{id}}" } }""";

    private static string[] NewIds(HackChangeList list) =>
        [.. list.Groups.SelectMany(static group => group.Entries).Where(static entry => entry.IsNew).Select(static entry => entry.Id)];

    private sealed class FakeSource(string? document) : IWhatsNewSource
    {
        public string? Document { get; set; } = document;

        public ValueTask<string?> GetDocumentAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Document);
    }

    /// <summary>Whole-file part of <see cref="IOsSettingsService"/>, one dictionary per home directory.</summary>
    private sealed class FakeSettings : IOsSettingsService
    {
        private readonly Dictionary<string, Dictionary<string, string>> _homes = new(StringComparer.Ordinal);

        public string? Home { get; set; } = "/home/alice";

        public Dictionary<string, string> Files => Home is null ? [] : FilesOf(Home);

        public int Writes { get; private set; }

        public event EventHandler<OsSettingsChangedEventArgs>? DocumentChanged { add { } remove { } }

        public string? CurrentHomeDirectory => Home;

        public ValueTask<string?> ReadFileAsync(OsSettingsFile file, CancellationToken cancellationToken = default) =>
            Home is null
                ? throw new InvalidOperationException("No session.")
                : ValueTask.FromResult(FilesOf(Home).GetValueOrDefault(file.RelativePath));

        public ValueTask WriteFileAsync(OsSettingsFile file, string content, IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal(OsSettingsLocation.UserState, file.Location);
            if (Home is null)
            {
                throw new InvalidOperationException("No session.");
            }

            FilesOf(Home)[file.RelativePath] = content;
            Writes++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<LayeredConfigSnapshot> ReadAsync(OsSettingsDocument document, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask SetAsync(OsSettingsDocument document, string qualifiedKey, string value, SettingLayer target = SettingLayer.User, IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask SetManyAsync(OsSettingsDocument document, IReadOnlyList<KeyValuePair<string, string>> values, SettingLayer target = SettingLayer.User, IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ResetAsync(OsSettingsDocument document, string qualifiedKey, SettingLayer target = SettingLayer.User, IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string> ReadValidatedFileAsync(OsSettingsFile file, ISettingsDocumentValidator validator, string defaultContent, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask WatchAsync(OsSettingsDocument document, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private Dictionary<string, string> FilesOf(string home)
        {
            if (!_homes.TryGetValue(home, out Dictionary<string, string>? files))
            {
                files = new(StringComparer.Ordinal);
                _homes[home] = files;
            }

            return files;
        }
    }
}
