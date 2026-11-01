using System.Security.Cryptography;
using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Apps;
using HackerOs.Platform.Core.Apps.Packages;

namespace HackerOs.Ecosystem.Tests;

/// <summary>ADR 0206: an installed app's wwwroot/ files are copied, checked, where the service worker serves them.</summary>
public sealed class InstalledAppAssetPublisherTests
{
    private const string AppId = "org.kazo.sample";
    private const string Folder = "/usr/lib/hackeros/apps/org.kazo.sample/1.0.0/wwwroot/";
    private static readonly byte[] Worker = Encoding.UTF8.GetBytes("importScripts('engine.js');");
    private static readonly byte[] Engine = Encoding.UTF8.GetBytes("self.engine = 1;");

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static AppManifest Manifest(params AssetManifest[] assets) => AppManifestJsonSerializer.NormalizeOptionalCollections(AppManifestJsonSerializer.DeserializeStrict(
        "{\"schemaVersion\":1,\"id\":\"" + AppId + "\",\"name\":\"Sample\",\"version\":\"1.0.0\",\"publisherId\":\"pub.kazo\","
        + "\"description\":\"A sample.\",\"kind\":\"window\",\"platform\":{\"supported\":[\"desktop\"],\"entryPoints\":[{\"platforms\":[\"desktop\"],"
        + "\"assembly\":\"Kazo.Sample.dll\",\"type\":\"Kazo.Sample.SampleWindow\"}]},\"sdkCompatibility\":{\"minimumVersion\":\"2.0.0\"},"
        + "\"presentation\":{\"category\":\"utilities\",\"launchVisibility\":\"visible\",\"iconAssetPaths\":[]},\"localizations\":[],\"capabilities\":[],"
        + "\"declaredTopicPermissions\":[],\"resources\":{\"baselineCpuWeight\":0,\"burstCpuWeight\":0,\"baselineMemoryWeight\":0,\"burstMemoryWeight\":0,"
        + "\"baselineStorageIoWeight\":0,\"burstStorageIoWeight\":0,\"baselineNetworkIoWeight\":0,\"burstNetworkIoWeight\":0},\"intents\":[],\"dependencies\":[],"
        + "\"assets\":[],\"fileHandlers\":[],\"singleInstancePerUser\":false,\"autoStart\":false,\"supportsBack\":false,\"execution\":\"mainThread\"}")) with { Assets = assets };

    private static (InstalledAppAssetPublisher Publisher, InstalledApp App, AppAssetStore Store) Create(MemoryCache cache, Dictionary<string, byte[]> files, AppManifest manifest)
    {
        InstalledApp app = new(new AppPackageSource("Kazo-ca", "Sample", SemanticVersion.Parse("1.0.0")), manifest);
        InstalledAppsBoot boot = new(BuildKnownLazyApps.Host);
        boot.Apply(new InstalledAppsImage([app], []), (path, _) => ValueTask.FromResult(files.TryGetValue(path.Value, out byte[]? bytes) ? bytes : null));
        AppAssetStore store = new(boot.Current.Catalog, new InstalledAppAssetSource(NoAppAssetSource.Instance, boot));
        return (new InstalledAppAssetPublisher(boot, cache, () => store), app, store);
    }

    [Fact]
    public async Task Every_declared_file_is_served_at_the_address_of_a_built_in_app_with_its_type()
    {
        MemoryCache cache = new();
        AppManifest manifest = Manifest(
            new AssetManifest("engine/worker.js", AssetKind.JavaScriptModule, Hash(Worker)),
            new AssetManifest("engine/engine.js", AssetKind.JavaScriptModule, Hash(Engine)));
        (InstalledAppAssetPublisher publisher, InstalledApp app, _) = Create(cache, new()
        {
            [Folder + "engine/worker.js"] = Worker,
            [Folder + "engine/engine.js"] = Engine,
        }, manifest);

        await publisher.PublishAsync(app);

        Assert.Equal(Worker, cache.Files["_content/Kazo.Sample/engine/worker.js"].Bytes);
        Assert.Equal("text/javascript", cache.Files["_content/Kazo.Sample/engine/engine.js"].ContentType);
        Assert.Equal(2, cache.Files.Count);
    }

    [Fact]
    public async Task Files_are_copied_once_per_session_and_stale_addresses_are_dropped()
    {
        MemoryCache cache = new();
        cache.Files["_content/Kazo.Removed/old.js"] = ([1], "text/javascript");
        AppManifest manifest = Manifest(new AssetManifest("engine/engine.js", AssetKind.JavaScriptModule, Hash(Engine)));
        (InstalledAppAssetPublisher publisher, InstalledApp app, _) = Create(cache, new() { [Folder + "engine/engine.js"] = Engine }, manifest);

        await publisher.PublishAsync(app);
        await publisher.PublishAsync(app);

        Assert.Equal(1, cache.Puts);
        Assert.Equal(1, cache.Prepares);
        Assert.False(cache.Files.ContainsKey("_content/Kazo.Removed/old.js"));
        Assert.True(cache.Files.ContainsKey("_content/Kazo.Sample/engine/engine.js"));
    }

    [Fact]
    public async Task A_damaged_file_is_not_served_and_its_old_copy_is_removed()
    {
        MemoryCache cache = new();
        cache.Files["_content/Kazo.Sample/engine/engine.js"] = (Engine, "text/javascript");
        AppManifest manifest = Manifest(
            new AssetManifest("engine/engine.js", AssetKind.JavaScriptModule, Hash(Engine)),
            new AssetManifest("engine/worker.js", AssetKind.JavaScriptModule, Hash(Worker)));
        (InstalledAppAssetPublisher publisher, InstalledApp app, AppAssetStore store) = Create(cache, new()
        {
            [Folder + "engine/engine.js"] = Encoding.UTF8.GetBytes("altered"),
            [Folder + "engine/worker.js"] = Worker,
        }, manifest);

        await publisher.PublishAsync(app);

        Assert.False(cache.Files.ContainsKey("_content/Kazo.Sample/engine/engine.js"));
        Assert.True(cache.Files.ContainsKey("_content/Kazo.Sample/engine/worker.js"));
        Assert.True(store.IsDamaged(AppId));
    }

    [Fact]
    public async Task Without_a_service_worker_nothing_is_copied_and_the_launch_goes_on()
    {
        MemoryCache cache = new() { CanServe = false };
        AppManifest manifest = Manifest(new AssetManifest("engine/engine.js", AssetKind.JavaScriptModule, Hash(Engine)));
        (InstalledAppAssetPublisher publisher, InstalledApp app, _) = Create(cache, new() { [Folder + "engine/engine.js"] = Engine }, manifest);

        await publisher.PublishAsync(app);

        Assert.Empty(cache.Files);
    }

    [Fact]
    public async Task The_scoped_css_bundle_is_linked_to_the_page_once_served()
    {
        MemoryCache cache = new();
        byte[] styles = Encoding.UTF8.GetBytes(".board[b-1x2y3z] { display: grid; }");
        AppManifest manifest = Manifest(
            new AssetManifest("Kazo.Sample.bundle.scp.css", AssetKind.Css, Hash(styles)),
            new AssetManifest("engine/engine.js", AssetKind.JavaScriptModule, Hash(Engine)));
        (InstalledAppAssetPublisher publisher, InstalledApp app, _) = Create(cache, new()
        {
            [Folder + "Kazo.Sample.bundle.scp.css"] = styles,
            [Folder + "engine/engine.js"] = Engine,
        }, manifest);

        await publisher.PublishAsync(app);
        await publisher.PublishAsync(app);

        Assert.Equal("text/css", cache.Files["_content/Kazo.Sample/Kazo.Sample.bundle.scp.css"].ContentType);
        Assert.Equal(["_content/Kazo.Sample/Kazo.Sample.bundle.scp.css"], cache.StyleSheets);
    }

    [Fact]
    public async Task An_app_without_a_scoped_css_bundle_links_no_style_sheet()
    {
        MemoryCache cache = new();
        byte[] other = Encoding.UTF8.GetBytes("body { color: red; }");
        AppManifest manifest = Manifest(new AssetManifest("css/site.css", AssetKind.Css, Hash(other)));
        (InstalledAppAssetPublisher publisher, InstalledApp app, _) = Create(cache, new() { [Folder + "css/site.css"] = other }, manifest);

        await publisher.PublishAsync(app);

        Assert.True(cache.Files.ContainsKey("_content/Kazo.Sample/css/site.css"));
        Assert.Empty(cache.StyleSheets);
    }

    [Fact]
    public async Task A_damaged_scoped_css_bundle_is_neither_served_nor_linked()
    {
        MemoryCache cache = new();
        byte[] styles = Encoding.UTF8.GetBytes(".board[b-1x2y3z] { display: grid; }");
        AppManifest manifest = Manifest(new AssetManifest("Kazo.Sample.bundle.scp.css", AssetKind.Css, Hash(styles)));
        (InstalledAppAssetPublisher publisher, InstalledApp app, _) = Create(cache, new()
        {
            [Folder + "Kazo.Sample.bundle.scp.css"] = Encoding.UTF8.GetBytes("altered"),
        }, manifest);

        await publisher.PublishAsync(app);

        Assert.Empty(cache.Files);
        Assert.Empty(cache.StyleSheets);
    }

    [Fact]
    public async Task A_style_sheet_that_cannot_be_linked_does_not_stop_the_launch()
    {
        MemoryCache cache = new() { LinkFails = true };
        byte[] styles = Encoding.UTF8.GetBytes(".board[b-1x2y3z] { display: grid; }");
        AppManifest manifest = Manifest(new AssetManifest("Kazo.Sample.bundle.scp.css", AssetKind.Css, Hash(styles)));
        (InstalledAppAssetPublisher publisher, InstalledApp app, _) = Create(cache, new() { [Folder + "Kazo.Sample.bundle.scp.css"] = styles }, manifest);

        await publisher.PublishAsync(app);

        Assert.True(cache.Files.ContainsKey("_content/Kazo.Sample/Kazo.Sample.bundle.scp.css"));
    }

    [Fact]
    public void The_scoped_css_bundle_is_named_after_the_entry_point_assembly()
    {
        InstalledApp app = new(new AppPackageSource("Kazo-ca", "Sample", SemanticVersion.Parse("1.0.0")), Manifest());

        Assert.Equal("Kazo.Sample.bundle.scp.css", InstalledAppAssetPublisher.GetScopedCssBundlePath(app));
    }

    [Theory]
    [InlineData("engine/stockfish.wasm", "application/wasm")]
    [InlineData("css/board.CSS", "text/css")]
    [InlineData("pieces/k.svg", "image/svg+xml")]
    [InlineData("data/book.bin", "application/octet-stream")]
    public void The_content_type_comes_from_the_extension(string path, string expected) =>
        Assert.Equal(expected, InstalledAppAssetContentTypes.For(path));

    private sealed class MemoryCache : IInstalledAppAssetCache
    {
        public Dictionary<string, (byte[] Bytes, string ContentType)> Files { get; } = new(StringComparer.Ordinal);

        public bool CanServe { get; init; } = true;

        public int Puts { get; private set; }

        public int Prepares { get; private set; }

        public ValueTask<bool> PrepareAsync(CancellationToken cancellationToken)
        {
            Prepares++;
            return ValueTask.FromResult(CanServe);
        }

        public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<string>>([.. Files.Keys]);

        public ValueTask PutAsync(string url, byte[] bytes, string contentType, CancellationToken cancellationToken)
        {
            Puts++;
            Files[url] = (bytes, contentType);
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(string url, CancellationToken cancellationToken)
        {
            Files.Remove(url);
            return ValueTask.CompletedTask;
        }

        public List<string> StyleSheets { get; } = [];

        public bool LinkFails { get; init; }

        public ValueTask LinkStyleSheetAsync(string url, CancellationToken cancellationToken)
        {
            if (LinkFails)
            {
                throw new InvalidOperationException("The page refused the style sheet.");
            }

            StyleSheets.Add(url);
            return ValueTask.CompletedTask;
        }
    }
}
