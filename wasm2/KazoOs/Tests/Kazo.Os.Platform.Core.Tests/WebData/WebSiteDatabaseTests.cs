using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.AppSdk.WebData;
using Kazo.Os.Platform.Core.Administration;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Tests.Diagnostics;
using Kazo.Os.Platform.Core.WebData;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Time;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Platform.Core.Tests.WebData;

/// <summary>
/// The OS side of ADR 0088: a site's browser database (name forced from the manifest, web apps only),
/// the Diagnostic listing and clearing, and the site debug log <c>/var/log/hackweb/&lt;id&gt;.log</c>.
/// </summary>
public sealed class WebSiteDatabaseTests
{
    private const string SiteId = "org.example.wiki";

    private static readonly HackWebDbSchemaDefinition Schema = new(1,
    [
        new HackWebDbMigrationStep(1, [new HackWebDbStoreDefinition("notes", "id", true, [])], [], [], [])
    ]);

    private static AppManifest Manifest(string id, AppKind kind) => new()
    {
        Id = id,
        Name = id,
        Version = "1.0.0",
        PublisherId = "org.test",
        Description = id,
        Kind = kind,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", $"{id}.EntryPoint"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None
    };

    private static HackWebDbRequest Add(string json) =>
        new(1, ["notes"], true, [new HackWebDbOperation(HackWebDbOperationKinds.Add, "notes", null, null, null, null, json)]);

    [Fact]
    public async Task A_web_app_gets_the_database_named_after_its_id_and_nothing_else()
    {
        InMemoryWebSiteDatabaseStore store = new();
        AppWebDatabaseGateway gateway = new(store, Manifest(SiteId, AppKind.Web));

        await gateway.OpenAsync(Schema);
        IReadOnlyList<string?> keys = await gateway.ExecuteAsync(Add("""{"title":"hello"}"""));

        Assert.Equal("hackweb.org.example.wiki", gateway.DatabaseName);
        Assert.Equal("1", keys[0]);
        HackWebSiteDatabaseInfo info = Assert.Single(await store.ListAsync());
        Assert.Equal("hackweb.org.example.wiki", info.Name);
        Assert.Equal(SiteId, info.AppId);
        Assert.Equal(1, info.Version);
        Assert.Equal(1, info.RecordCount);
        Assert.True(info.SizeBytes > 0);
    }

    [Theory]
    [InlineData(AppKind.Window)]
    [InlineData(AppKind.Terminal)]
    [InlineData(AppKind.Service)]
    public async Task Other_apps_have_no_browser_database(AppKind kind)
    {
        AppWebDatabaseGateway gateway = new(new InMemoryWebSiteDatabaseStore(), Manifest("org.example.app", kind));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gateway.OpenAsync(Schema).AsTask());
    }

    [Fact]
    public async Task A_host_without_site_databases_says_so()
    {
        AppWebDatabaseGateway gateway = new(null, Manifest(SiteId, AppKind.Web));

        await Assert.ThrowsAsync<NotSupportedException>(() => gateway.OpenAsync(Schema).AsTask());
    }

    [Fact]
    public async Task A_deleted_database_is_empty_when_the_site_opens_it_again()
    {
        InMemoryWebSiteDatabaseStore store = new();
        AppWebDatabaseGateway gateway = new(store, Manifest(SiteId, AppKind.Web));
        await gateway.OpenAsync(Schema);
        await gateway.ExecuteAsync(Add("""{"title":"hello"}"""));

        await store.DeleteAsync("hackweb.org.example.wiki");
        Assert.Empty(await store.ListAsync());
        // The running site keeps its schema: its next request finds an empty database, as after a browser "clear site data".
        IReadOnlyList<string?> keys = await gateway.ExecuteAsync(Add("""{"title":"again"}"""));

        Assert.Equal("1", keys[0]);
        Assert.Equal(1, Assert.Single(await store.ListAsync()).RecordCount);
    }

    [Theory]
    [InlineData("other-db")]
    [InlineData("hackweb.")]
    [InlineData("")]
    public async Task Only_site_databases_can_be_deleted(string name)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new InMemoryWebSiteDatabaseStore().DeleteAsync(name).AsTask());
    }

    [Fact]
    public async Task The_Diagnostic_listing_and_clearing_need_storage_inspection()
    {
        InMemoryWebSiteDatabaseStore store = new();
        await new AppWebDatabaseGateway(store, Manifest(SiteId, AppKind.Web)).OpenAsync(Schema);
        AppStorageDiagnosticsGateway denied = new(null, new Capabilities(), store);
        AppStorageDiagnosticsGateway granted = new(null, new Capabilities(AppCapabilities.SystemStorageInspect), store);

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => denied.GetWebSiteDatabasesAsync().AsTask());
        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => denied.DeleteWebSiteDatabaseAsync("hackweb.org.example.wiki").AsTask());
        Assert.Single(await granted.GetWebSiteDatabasesAsync());
        await granted.DeleteWebSiteDatabaseAsync("hackweb.org.example.wiki");
        Assert.Empty(await granted.GetWebSiteDatabasesAsync());
    }

    [Fact]
    public void Only_a_web_app_writes_a_site_debug_log()
    {
        BoundedDiagnosticSink memory = new(10);
        ManualSimulationClock clock = new(new DateTimeOffset(2026, 10, 17, 1, 0, 0, TimeSpan.Zero), TimeSpan.FromMilliseconds(1));
        AppLoggingGateway window = new(memory, clock, "org.example.notes");
        AppLoggingGateway system = new(memory, clock, "org.hackeros.hackwebd", isSystemApp: true);

        Assert.Throws<UnauthorizedAccessException>(() => window.AppendToSiteDebugLog("hello"));
        Assert.Throws<UnauthorizedAccessException>(() => system.AppendToSiteDebugLog("hello"));
    }

    [Fact]
    public async Task A_site_debug_log_goes_to_var_log_hackweb_with_line_breaks_escaped()
    {
        LogTestFileSystem fs = await LogTestFileSystem.CreateAsync();
        BoundedDiagnosticSink memory = new(10);
        LogFileWriter writer = new(fs.Clock, memory);
        AppLoggingGateway gateway = new(memory, fs.Clock, SiteId, writer, processId: 9, isWebApp: true);

        gateway.AppendToSiteDebugLog("2026-10-17T01:22:14.123Z info: Wiki[0] started");
        gateway.AppendToSiteDebugLog("fail: boom\n   at Wiki.Run()");
        await writer.AttachAsync(fs.Service);

        string[] lines = await fs.ReadLinesAsync($"/var/log/hackweb/{SiteId}.log");
        Assert.Equal(["2026-10-17T01:22:14.123Z info: Wiki[0] started", "fail: boom\\n   at Wiki.Run()"], lines);
    }

    private sealed class Capabilities(params string[] granted) : ICapabilityChecker
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
