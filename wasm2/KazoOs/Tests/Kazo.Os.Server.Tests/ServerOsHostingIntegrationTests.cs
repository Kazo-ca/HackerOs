using System.Net;
using System.Net.Http.Json;
using Kazo.Os.Platform.Core.ServerConnection;
using Kazo.Os.Server.Contracts.Hosting;
using Kazo.Os.Server.Contracts.Identity;
using Kazo.Os.Server.Contracts.Sync;
using Kazo.Os.Simulation.Abstractions.ServerConnection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Kazo.Os.Server.Tests;

/// <summary>
/// ADR 0130 and ADR 0134: the server sends the Ecosystem's OS in WebAssembly, answers the host
/// discovery as a server, and the OS's own HTTP clients reach its API (they replaced the in-process
/// clients of ADR 0036, removed with the Interactive Server rendering of ADR 0027).
/// </summary>
[Collection(ServerEnvironmentCollection.Name)]
public sealed class ServerOsHostingIntegrationTests : IAsyncLifetime
{
    private static readonly Uri ServerUrl = new("http://localhost/");

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"hackeros-server-os-integration-{Guid.NewGuid():N}.db");
    private readonly string _backupRoot = Path.Combine(
        Path.GetTempPath(),
        $"hackeros-server-os-backups-{Guid.NewGuid():N}");
    private string? _previousConnectionString;
    private string? _previousBackupRoot;
    private ServerApplicationFactory? _factory;

    public Task InitializeAsync()
    {
        _previousConnectionString = Environment.GetEnvironmentVariable("HACKEROS_ConnectionStrings__HackerOsDb");
        _previousBackupRoot = Environment.GetEnvironmentVariable("HACKEROS_ServerBackup__Root");
        Environment.SetEnvironmentVariable("HACKEROS_ConnectionStrings__HackerOsDb", $"Data Source={_databasePath}");
        Environment.SetEnvironmentVariable("HACKEROS_ServerBackup__Root", _backupRoot);
        _factory = new ServerApplicationFactory();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        Environment.SetEnvironmentVariable("HACKEROS_ConnectionStrings__HackerOsDb", _previousConnectionString);
        Environment.SetEnvironmentVariable("HACKEROS_ServerBackup__Root", _previousBackupRoot);
    }

    [Fact]
    public async Task Root_ServesTheEcosystemWebAssemblyPage_AndApiRemainsReachable()
    {
        using HttpClient client = Factory.CreateClient();

        foreach (string path in new[] { "/", "/a/deep/link" })
        {
            using HttpResponseMessage page = await client.GetAsync(path);
            page.EnsureSuccessStatusCode();
            string body = await page.Content.ReadAsStringAsync();
            // The standalone PWA's page: the WebAssembly bootstrap, never the Interactive Server one.
            Assert.Contains("_framework/blazor.webassembly", body, StringComparison.Ordinal);
            Assert.DoesNotContain("blazor.web.js", body, StringComparison.Ordinal);
            Assert.Contains("hackeros-boot-error", body, StringComparison.Ordinal);
        }

        using HttpResponseMessage manifest = await client.GetAsync("/manifest.webmanifest");
        manifest.EnsureSuccessStatusCode();

        // The page links the Ecosystem's scoped-CSS bundle; the server answers with its own (ADR 0137 §2).
        using HttpResponseMessage styles = await client.GetAsync("/Kazo.Os.Ecosystem.styles.css");
        styles.EnsureSuccessStatusCode();
        Assert.Contains("@import", await styles.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using HttpResponseMessage health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();
        Assert.Contains("\"status\":\"healthy\"", await health.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // An unknown API route stays a 404: it never receives the OS page.
        using HttpResponseMessage unknown = await client.GetAsync("/api/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task HostDiscovery_SaysServer_OnBothPaths_AndIsNeverCached()
    {
        using HttpClient client = Factory.CreateClient();

        foreach (string path in new[] { "/" + HostInfoRoutes.DiscoveryFile, HostInfoRoutes.ApiPath })
        {
            using HttpResponseMessage response = await client.GetAsync(path);
            response.EnsureSuccessStatusCode();
            Assert.True(response.Headers.CacheControl?.NoStore, path);
            HostInfoResponse? info = await response.Content.ReadFromJsonAsync(HostingContractsJsonContext.Default.HostInfoResponse);
            Assert.NotNull(info);
            Assert.True(info.IsServer, path);
            Assert.Equal(HostInfoRoutes.ServerProduct, info.Product);
            Assert.Equal("1.0", info.ApiVersion);
        }

        // What the OS itself runs at boot (ADR 0134 §1): the origin that served it is a server.
        HttpHostInfoClient discovery = new(client, ServerUrl);
        Assert.Equal(ServerUrl, await discovery.FindServingServerAsync());
        Assert.True((await discovery.GetHostInfoAsync(ServerUrl)).IsServer);
    }

    [Fact]
    public async Task AccountClient_CreatesAnAccount_ThenAnotherDeviceLogsIn_AndBadCredentialsAreRefused()
    {
        using HttpClient client = Factory.CreateClient();
        HttpAccountClient accounts = new(client);
        string login = $"user-{Guid.NewGuid():N}"[..20];

        // Device A creates the account; device B (another browser) logs into it later. Both derive
        // the same client salt (ADR 0137 §4), so the second login matches the stored hash.
        ServerConnectionService deviceA = new(new MemoryConnectionRepository(), accounts);
        ServerConnectionState created = await deviceA.ConnectWithNewAccountAsync(ServerUrl, login, "Correct-Horse-1");
        ServerConnectionService deviceB = new(new MemoryConnectionRepository(), accounts);
        ServerConnectionState loggedIn = await deviceB.ConnectWithExistingAccountAsync(ServerUrl, login, "Correct-Horse-1");
        Assert.Equal(created.AccountId, loggedIn.AccountId);
        Assert.NotEqual(created.DeviceId, loggedIn.DeviceId);
        Assert.NotNull(await deviceB.EnsureAccessTokenAsync());

        await Assert.ThrowsAsync<ServerConnectionException>(() =>
            new ServerConnectionService(new MemoryConnectionRepository(), accounts)
                .ConnectWithExistingAccountAsync(ServerUrl, login, "Wrong-Password-1"));
        await Assert.ThrowsAsync<ServerConnectionException>(() =>
            accounts.CreateAccountAsync(ServerUrl, new CreateAccountRequest(login, "h", "s", "Again", "fp")));
    }

    [Fact]
    public async Task SyncClient_RefusesAnInvalidToken()
    {
        using HttpClient client = Factory.CreateClient();
        HttpSyncClient sync = new(client);

        await Assert.ThrowsAsync<ServerConnectionException>(() =>
            sync.PullAsync(ServerUrl, "not-a-real-token", new PullRequest("filesystem", null, 100)));
    }

    private ServerApplicationFactory Factory => Assert.IsType<ServerApplicationFactory>(_factory);

    private sealed class MemoryConnectionRepository : IServerConnectionRepository
    {
        private ServerConnectionState? _state;

        public ValueTask<ServerConnectionState?> GetAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_state);

        public ValueTask SetAsync(ServerConnectionState state, CancellationToken cancellationToken = default)
        {
            _state = state;
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync(CancellationToken cancellationToken = default)
        {
            _state = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ServerApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // The OS's files are static web assets of the referenced Ecosystem project: outside a
            // publish, ASP.NET Core only maps them from the build output in Development (the files
            // themselves stay in each project's wwwroot and obj/ until published).
            builder.UseEnvironment("Development");
        }
    }
}
