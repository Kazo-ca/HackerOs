using System.Threading.Channels;
using Kazo.Os.Platform.Core.ServerConnection;
using Kazo.Os.Server.Contracts.Hosting;
using Kazo.Os.Simulation.Abstractions.ServerConnection;
using Xunit;

namespace Kazo.Os.Platform.Core.Tests.ServerConnection;

/// <summary>
/// ADR 0134: the OS connects by itself to the server that served it, shows the state in the
/// notification area, retries a lost server, and never depends on it.
/// </summary>
public sealed class ServerPresenceServiceTests
{
    private static readonly Uri Origin = new("https://os.example/");
    private static readonly Uri Linked = new("https://linked.example/");

    [Fact]
    public async Task Os_served_by_a_server_connects_without_an_account_and_without_asking()
    {
        Connection connection = new();
        HostInfo hostInfo = new(servingServer: Origin);
        await using ServerPresenceService service = new(connection, hostInfo, delay: Waits.Never);

        await service.StartAsync();

        Assert.Equal(ServerPresenceStatus.ConnectedAnonymous, service.Current.Status);
        Assert.Equal(Origin, service.Current.ServerUrl);
        Assert.True(service.Current.ServedByServer);
        Assert.Null(service.Current.AccountId);
        Assert.Equal(0, connection.ConnectCalls);
    }

    [Fact]
    public async Task Static_host_without_a_linked_server_shows_nothing()
    {
        await using ServerPresenceService service = new(new Connection(), new HostInfo(servingServer: null), delay: Waits.Never);

        await service.StartAsync();

        Assert.Equal(ServerPresenceStatus.NotConfigured, service.Current.Status);
        Assert.Null(service.Current.ServerUrl);
    }

    [Fact]
    public async Task A_server_linked_by_hand_is_connected_with_its_account_on_any_host()
    {
        Connection connection = new() { State = Linked };
        await using ServerPresenceService service = new(connection, new HostInfo(servingServer: null), delay: Waits.Never);

        await service.StartAsync();

        Assert.Equal(ServerPresenceStatus.Connected, service.Current.Status);
        Assert.Equal(Linked, service.Current.ServerUrl);
        Assert.False(service.Current.ServedByServer);
        Assert.NotNull(service.Current.AccountId);
    }

    [Fact]
    public async Task Unreachable_server_is_offline_and_retried_with_a_growing_delay_until_it_answers()
    {
        HostInfo hostInfo = new(servingServer: Origin) { Reachable = false };
        Waits waits = new();
        await using ServerPresenceService service = new(new Connection(), hostInfo, delay: waits.WaitAsync);

        await service.StartAsync();
        Assert.Equal(ServerPresenceStatus.Offline, service.Current.Status);

        TimeSpan first = await waits.NextAsync();
        waits.Release();
        TimeSpan second = await waits.NextAsync();
        Assert.True(second > first, $"{second} should be longer than {first}");

        hostInfo.Reachable = true;
        waits.Release();
        TimeSpan heartbeat = await waits.NextAsync();
        Assert.Equal(ServerPresenceStatus.ConnectedAnonymous, service.Current.Status);
        Assert.Equal(TimeSpan.FromSeconds(30), heartbeat);
    }

    [Fact]
    public async Task Incompatible_api_version_is_an_error()
    {
        HostInfo hostInfo = new(servingServer: Origin) { ApiVersion = "2.0" };
        await using ServerPresenceService service = new(new Connection(), hostInfo, delay: Waits.Never);

        await service.StartAsync();

        Assert.Equal(ServerPresenceStatus.Error, service.Current.Status);
        Assert.Contains("2.0", service.Current.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Identify_links_an_account_on_the_current_server_and_turns_connected()
    {
        Connection connection = new();
        await using ServerPresenceService service = new(connection, new HostInfo(servingServer: Origin), delay: Waits.Never);
        await service.StartAsync();

        await service.IdentifyAsync("benoit", "Secret-1", createAccount: true);

        Assert.Equal(1, connection.ConnectCalls);
        Assert.Equal(Origin, connection.State);
        Assert.Equal(ServerPresenceStatus.Connected, service.Current.Status);
    }

    [Fact]
    public async Task Auto_connect_false_ignores_the_serving_server()
    {
        HostInfo hostInfo = new(servingServer: Origin);
        await using ServerPresenceService service = new(
            new Connection(), hostInfo, _ => ValueTask.FromResult<string?>("[server]\nauto-connect=false\n"), delay: Waits.Never);

        await service.StartAsync();

        Assert.Equal(ServerPresenceStatus.NotConfigured, service.Current.Status);
        Assert.Equal(0, hostInfo.DiscoveryCalls);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("# nothing\n", true)]
    [InlineData("auto-connect=false\n", false)]
    [InlineData("[server]\nauto-connect=off\n", false)]
    [InlineData("[server]\nauto-connect=0\n", false)]
    [InlineData("[server]\nauto-connect=true\n", true)]
    [InlineData("[server]\nauto-connect=maybe\n", true)]
    public void Auto_connect_is_on_unless_explicitly_turned_off(string? content, bool expected) =>
        Assert.Equal(expected, ServerPresenceService.ParseAutoConnect(content));

    [Fact]
    public void Account_salt_is_the_same_on_every_device_and_differs_per_account()
    {
        Assert.Equal(ServerConnectionService.DeriveAccountSalt("benoit"), ServerConnectionService.DeriveAccountSalt("benoit"));
        Assert.NotEqual(ServerConnectionService.DeriveAccountSalt("benoit"), ServerConnectionService.DeriveAccountSalt("alice"));
        Assert.Equal(16, ServerConnectionService.DeriveAccountSalt("benoit").Length);
    }

    /// <summary>Records each wait the loop asks for, and lets the test release it.</summary>
    private sealed class Waits
    {
        private readonly Channel<TimeSpan> _requested = Channel.CreateUnbounded<TimeSpan>();
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>A wait that never ends: the test looks at the first check only.</summary>
        public static Task Never(TimeSpan wait, CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);

        public async Task WaitAsync(TimeSpan wait, CancellationToken cancellationToken)
        {
            TaskCompletionSource release = _release;
            await _requested.Writer.WriteAsync(wait, cancellationToken);
            await release.Task.WaitAsync(cancellationToken);
        }

        public async Task<TimeSpan> NextAsync() =>
            await _requested.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public void Release()
        {
            TaskCompletionSource released = _release;
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            released.SetResult();
        }
    }

    private sealed class HostInfo(Uri? servingServer) : IHostInfoClient
    {
        public bool Reachable { get; set; } = true;

        public string ApiVersion { get; set; } = "1.0";

        public int DiscoveryCalls { get; private set; }

        public Task<Uri?> FindServingServerAsync(CancellationToken cancellationToken = default)
        {
            DiscoveryCalls++;
            return Task.FromResult(servingServer);
        }

        public Task<HostInfoResponse> GetHostInfoAsync(Uri serverBaseUrl, CancellationToken cancellationToken = default) =>
            Reachable
                ? Task.FromResult(new HostInfoResponse(HostInfoRoutes.ServerProduct, true, "1.0.0", ApiVersion))
                : Task.FromException<HostInfoResponse>(new HttpRequestException("connection refused"));
    }

    private sealed class Connection : IServerConnectionService
    {
        public Uri? State { get; set; }

        public int ConnectCalls { get; private set; }

        public ValueTask<ServerConnectionState?> GetStateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(State is null
                ? null
                : new ServerConnectionState(Guid.NewGuid(), Guid.NewGuid(), State.ToString(), "fp", "refresh", DateTimeOffset.UtcNow.AddDays(1)));

        public Task<ServerConnectionState> ConnectWithNewAccountAsync(Uri serverBaseUrl, string username, string password, CancellationToken cancellationToken = default) =>
            Link(serverBaseUrl);

        public Task<ServerConnectionState> ConnectWithExistingAccountAsync(Uri serverBaseUrl, string username, string password, CancellationToken cancellationToken = default) =>
            Link(serverBaseUrl);

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
        {
            State = null;
            return ValueTask.CompletedTask;
        }

        public Task<string?> EnsureAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(State is null ? null : "token");

        private async Task<ServerConnectionState> Link(Uri server)
        {
            ConnectCalls++;
            State = server;
            return (await GetStateAsync())!;
        }
    }
}
