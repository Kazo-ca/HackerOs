using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.AppSdk.Administration;
using HackerOs.Platform.Core.Administration;
using HackerOs.Platform.Core.ServerConnection;
using HackerOs.Server.Contracts.Hosting;
using HackerOs.Server.Contracts.Identity;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.ServerConnection;
using Xunit;

namespace HackerOs.Platform.Core.Tests.Administration;

/// <summary>
/// ADR 0191 §6: Settings &gt; Local server tests an address and pairs this device with a one-time
/// code through <see cref="IAppServerConnectionGateway"/>; the tokens stay inside the OS.
/// </summary>
public sealed class LocalServerGatewayTests
{
    private static readonly Uri Server = new("http://localhost:5888/");

    [Fact]
    public async Task Pairing_stores_the_device_link_and_returns_the_public_state()
    {
        Repository repository = new();
        AccountClient accounts = new();
        ServerConnectionService connection = new(repository, accounts);
        AppServerConnectionGateway gateway = new(
            new ServerConnectionGatewayServices(() => connection), new Capabilities(AppCapabilities.ServerConnectionManage));

        HackServerConnectionState state = await gateway.PairAsync(Server, " h9r4-jyyg ");

        Assert.True(state.IsConnected);
        Assert.Equal(Server, state.ServerUrl);
        Assert.Equal(AccountClient.Account, state.AccountId);
        Assert.Equal("h9r4-jyyg", accounts.Request!.Code);
        Assert.Equal(accounts.Request.DeviceFingerprint, repository.State!.DeviceFingerprint);
        Assert.Equal("refresh-1", repository.State.RefreshTokenOpaque);
        Assert.Equal("access-1", await connection.EnsureAccessTokenAsync());
    }

    [Fact]
    public async Task A_refused_code_is_a_server_connection_error()
    {
        AccountClient accounts = new() { Refuse = true };
        AppServerConnectionGateway gateway = new(
            new ServerConnectionGatewayServices(() => new ServerConnectionService(new Repository(), accounts)),
            new Capabilities(AppCapabilities.ServerConnectionManage));

        HackServerConnectionException error = await Assert.ThrowsAsync<HackServerConnectionException>(
            () => gateway.PairAsync(Server, "AAAA-AAAA").AsTask());

        Assert.Contains("Could not pair", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pairing_and_testing_need_the_capability()
    {
        AppServerConnectionGateway denied = new(
            new ServerConnectionGatewayServices(() => new ServerConnectionService(new Repository(), new AccountClient()))
            {
                HostInfo = () => new HostInfo(new HostInfoResponse("HackerOS Server", true, "2.1.0", "1.0"))
            },
            new Capabilities());

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => denied.PairAsync(Server, "AAAA-AAAA").AsTask());
        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => denied.TestAsync(Server).AsTask());
    }

    [Fact]
    public async Task Testing_reports_a_server_an_other_site_and_an_unreachable_address()
    {
        static AppServerConnectionGateway With(HostInfo hostInfo) => new(
            new ServerConnectionGatewayServices(() => throw new InvalidOperationException()) { HostInfo = () => hostInfo },
            new Capabilities(AppCapabilities.ServerConnectionManage));

        HackServerProbe found = await With(new HostInfo(new HostInfoResponse("HackerOS Server", true, "2.1.0", "1.0"))).TestAsync(Server);
        HackServerProbe other = await With(new HostInfo(new ServerConnectionException("answered 404: not a HackerOS server"))).TestAsync(Server);
        HackServerProbe offline = await With(new HostInfo(new HttpRequestException("Connection refused"))).TestAsync(Server);

        Assert.Equal(new HackServerProbe(true, "2.1.0", "1.0", null), found);
        Assert.False(other.IsServer);
        Assert.Contains("not a HackerOS server", other.Error, StringComparison.Ordinal);
        Assert.False(offline.IsServer);
        Assert.Contains("could not be reached", offline.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_without_the_discovery_client_cannot_test()
    {
        AppServerConnectionGateway gateway = new(services: null, new Capabilities(AppCapabilities.ServerConnectionManage));

        HackServerProbe probe = await gateway.TestAsync(Server);

        Assert.False(probe.IsServer);
        Assert.NotNull(probe.Error);
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

    private sealed class Repository : IServerConnectionRepository
    {
        public ServerConnectionState? State { get; private set; }

        public ValueTask<ServerConnectionState?> GetAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(State);

        public ValueTask SetAsync(ServerConnectionState state, CancellationToken cancellationToken = default)
        {
            State = state;
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync(CancellationToken cancellationToken = default)
        {
            State = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AccountClient : IAccountClient
    {
        public static readonly Guid Account = Guid.NewGuid();

        public bool Refuse { get; init; }

        public PairDeviceRequest? Request { get; private set; }

        public Task<CreateAccountResponse> CreateAccountAsync(Uri serverBaseUrl, CreateAccountRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LoginResponse> LoginAsync(Uri serverBaseUrl, LoginRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RefreshTokenResponse> RefreshAsync(Uri serverBaseUrl, RefreshTokenRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LoginResponse> PairAsync(Uri serverBaseUrl, PairDeviceRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            if (Refuse)
            {
                throw new ServerConnectionException("The optional server rejected the request (401 Unauthorized).");
            }

            return Task.FromResult(new LoginResponse(
                Account, Guid.NewGuid(), "access-1", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(15), DateTimeOffset.UtcNow.AddDays(30)));
        }
    }

    private sealed class HostInfo : IHostInfoClient
    {
        private readonly HostInfoResponse? _answer;
        private readonly Exception? _failure;

        public HostInfo(HostInfoResponse answer) => _answer = answer;

        public HostInfo(Exception failure) => _failure = failure;

        public Task<Uri?> FindServingServerAsync(CancellationToken cancellationToken = default) => Task.FromResult<Uri?>(null);

        public Task<HostInfoResponse> GetHostInfoAsync(Uri serverBaseUrl, CancellationToken cancellationToken = default) =>
            _failure is null ? Task.FromResult(_answer!) : Task.FromException<HostInfoResponse>(_failure);
    }
}
