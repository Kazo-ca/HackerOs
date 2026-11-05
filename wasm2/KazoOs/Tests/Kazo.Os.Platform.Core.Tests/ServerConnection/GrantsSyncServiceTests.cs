using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Platform.Core.ServerConnection;
using Kazo.Os.Server.Contracts.Sync;
using Kazo.Os.Simulation.Abstractions.ServerConnection;
using Kazo.Os.Simulation.Abstractions.Sync;

namespace Kazo.Os.Platform.Core.Tests.ServerConnection;

/// <summary>
/// Exercises <see cref="GrantsSyncService"/> (ADR 0031) against fakes for every dependency —
/// mirrors <c>SettingsSyncServiceTests</c>'s/<c>FileSystemSyncServiceTests</c>'s approach. Pull-only:
/// there is no push to test, by design (ADR 0031 Decision 1).
/// </summary>
public sealed class GrantsSyncServiceTests
{
    private static readonly Uri ServerBaseUrl = new("https://example.test/");
    private static readonly ServerConnectionState Connected = new(
        Guid.NewGuid(), Guid.NewGuid(), ServerBaseUrl.ToString(), "fingerprint", "refresh-token", DateTimeOffset.UtcNow.AddDays(30));

    [Fact]
    public async Task PullAsync_WhenDisconnected_NeverCallsSyncClient()
    {
        var grants = new FakeGrantRepository();
        var syncClient = new FakeSyncClient();
        var service = CreateService(grants, syncClient, connectionState: null);

        await service.PullAsync();

        Assert.Empty(syncClient.PullCalls);
        Assert.Empty(grants.ImportCalls);
    }

    [Fact]
    public async Task PullAsync_NewGrant_ImportsUnderTheRecordId()
    {
        var grants = new FakeGrantRepository();
        Guid recordId = Guid.NewGuid();
        var payload = new GrantsSyncPayload(
            "org.kazoos.browser", "user-1", AppCapabilities.FileSystemUserHomeRead,
            nameof(CapabilityGrantSource.AdministratorApproval), [], IsRevoked: false);
        var syncClient = new FakeSyncClient
        {
            PullResponses = new Queue<PullResponse>(
            [
                new PullResponse(
                    SyncDomain.Grants,
                    [Envelope(recordId, payload)],
                    NextCursor: null,
                    HasMore: false,
                    DateTimeOffset.UtcNow)
            ])
        };
        var service = CreateService(grants, syncClient, Connected);

        await service.PullAsync();

        var call = Assert.Single(grants.ImportCalls);
        Assert.Equal(recordId, call.Id.Value);
        Assert.Equal("org.kazoos.browser", call.AppId);
        Assert.Equal("user-1", call.UserId);
        Assert.Equal(AppCapabilities.FileSystemUserHomeRead, call.Capability);
        Assert.Equal(CapabilityGrantSource.AdministratorApproval, call.Source);
        Assert.False(call.IsRevoked);
    }

    [Fact]
    public async Task PullAsync_RevokedGrant_ImportsWithIsRevokedTrue()
    {
        var grants = new FakeGrantRepository();
        Guid recordId = Guid.NewGuid();
        var payload = new GrantsSyncPayload(
            "org.kazoos.browser", "user-1", AppCapabilities.FileSystemUserHomeRead,
            nameof(CapabilityGrantSource.AdministratorApproval), [], IsRevoked: true);
        var syncClient = new FakeSyncClient
        {
            PullResponses = new Queue<PullResponse>(
            [
                new PullResponse(
                    SyncDomain.Grants, [Envelope(recordId, payload)], null, false, DateTimeOffset.UtcNow)
            ])
        };
        var service = CreateService(grants, syncClient, Connected);

        await service.PullAsync();

        Assert.True(Assert.Single(grants.ImportCalls).IsRevoked);
    }

    [Fact]
    public async Task PullAsync_Tombstone_IsSkipped()
    {
        var grants = new FakeGrantRepository();
        var tombstone = new SyncRecordEnvelope(
            Guid.NewGuid(), SyncDomain.Grants, 1, Connected.AccountId, Connected.DeviceId,
            1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "hash", true, null);
        var syncClient = new FakeSyncClient
        {
            PullResponses = new Queue<PullResponse>(
            [
                new PullResponse(SyncDomain.Grants, [tombstone], null, false, DateTimeOffset.UtcNow)
            ])
        };
        var service = CreateService(grants, syncClient, Connected);

        await service.PullAsync();

        Assert.Empty(grants.ImportCalls);
    }

    [Fact]
    public async Task PullAsync_WithConstraints_ReconstructsAllThreeKinds()
    {
        var grants = new FakeGrantRepository();
        Guid recordId = Guid.NewGuid();
        var payload = new GrantsSyncPayload(
            "org.kazoos.browser", "user-1", AppCapabilities.FileSystemUserHomeRead,
            nameof(CapabilityGrantSource.AdministratorApproval),
            [
                new GrantConstraintPayload(nameof(CapabilityConstraintKind.VirtualPath), "/home/user-1", true, null, null, null),
                new GrantConstraintPayload(nameof(CapabilityConstraintKind.NetworkHost), null, null, "example.test", null, null),
                new GrantConstraintPayload(nameof(CapabilityConstraintKind.NetworkPort), null, null, null, 80, 443)
            ],
            IsRevoked: false);
        var syncClient = new FakeSyncClient
        {
            PullResponses = new Queue<PullResponse>(
            [
                new PullResponse(SyncDomain.Grants, [Envelope(recordId, payload)], null, false, DateTimeOffset.UtcNow)
            ])
        };
        var service = CreateService(grants, syncClient, Connected);

        await service.PullAsync();

        var constraints = Assert.Single(grants.ImportCalls).Constraints!.ToArray();
        Assert.Equal(3, constraints.Length);
        var path = Assert.IsType<VirtualPathCapabilityConstraint>(constraints[0]);
        Assert.Equal("/home/user-1", path.Path.Value);
        Assert.True(path.IncludeDescendants);
        var host = Assert.IsType<NetworkHostCapabilityConstraint>(constraints[1]);
        Assert.Equal("example.test", host.Host);
        var port = Assert.IsType<NetworkPortCapabilityConstraint>(constraints[2]);
        Assert.Equal(80, port.MinimumPort);
        Assert.Equal(443, port.MaximumPort);
    }

    [Fact]
    public async Task PullAsync_AdvancesCursorOnlyWhenGiven()
    {
        var grants = new FakeGrantRepository();
        var cursors = new InMemorySyncCursorRepository();
        var syncClient = new FakeSyncClient
        {
            PullResponses = new Queue<PullResponse>(
            [
                new PullResponse(SyncDomain.Grants, [], NextCursor: null, HasMore: false, DateTimeOffset.UtcNow)
            ])
        };
        var service = CreateService(grants, syncClient, Connected, cursors);

        await service.PullAsync();

        Assert.Null(await cursors.GetCursorAsync(SyncDomain.Grants));
    }

    private static SyncRecordEnvelope Envelope(Guid recordId, GrantsSyncPayload payload) => new(
        recordId, SyncDomain.Grants, 1, Connected.AccountId, Connected.DeviceId,
        1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "hash", false,
        System.Text.Json.JsonSerializer.Serialize(payload, GrantsSyncContractsJsonContext.Default.GrantsSyncPayload));

    private static GrantsSyncService CreateService(
        FakeGrantRepository grants,
        FakeSyncClient syncClient,
        ServerConnectionState? connectionState,
        InMemorySyncCursorRepository? cursorRepository = null) =>
        new(
            grants,
            new FakeServerConnectionService(connectionState),
            syncClient,
            cursorRepository ?? new InMemorySyncCursorRepository());

    private sealed record ImportCall(
        CapabilityGrantId Id, string AppId, string UserId, string Capability,
        CapabilityGrantSource Source, IEnumerable<CapabilityConstraint>? Constraints,
        bool IsRevoked, AppAuthority ActingAuthority);

    private sealed class FakeGrantRepository : IPersistentCapabilityGrantRepository
    {
        public List<ImportCall> ImportCalls { get; } = [];

        public ValueTask<long> GetCurrentPolicyRevisionAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(1L);

        public ValueTask<CapabilityGrantMutationResult> GrantAsync(
            string appId, string userId, string capability, CapabilityGrantSource source,
            AppAuthority actingAuthority, IEnumerable<CapabilityConstraint>? constraints = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Grants sync never pushes/grants locally — pull-only (ADR 0031).");

        public ValueTask<CapabilityGrantMutationResult> RevokeAsync(
            CapabilityGrantId grantId, AppAuthority actingAuthority, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public ValueTask<CapabilityGrantMutationResult> ImportAsync(
            CapabilityGrantId id, string appId, string userId, string capability,
            CapabilityGrantSource source, IEnumerable<CapabilityConstraint>? constraints,
            bool isRevoked, AppAuthority actingAuthority, CancellationToken cancellationToken = default)
        {
            ImportCalls.Add(new ImportCall(id, appId, userId, capability, source, constraints, isRevoked, actingAuthority));
            return ValueTask.FromResult(new CapabilityGrantMutationResult(
                isRevoked ? CapabilityGrantMutationStatus.Revoked : CapabilityGrantMutationStatus.Granted,
                null,
                1));
        }

        public ValueTask<CapabilityPolicyEvaluation> EvaluateAsync(
            string appId, string userId, string capability, AppAuthority actingAuthority,
            AppAuthority requiredAuthority, CapabilityResourceCandidate? resourceCandidate = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeServerConnectionService(ServerConnectionState? state) : IServerConnectionService
    {
        public ValueTask<ServerConnectionState?> GetStateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(state);

        public Task<ServerConnectionState> ConnectWithNewAccountAsync(
            Uri serverBaseUrl, string username, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ServerConnectionState> ConnectWithExistingAccountAsync(
            Uri serverBaseUrl, string username, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public Task<string?> EnsureAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(state is null ? null : "access-token");
    }

    private sealed class FakeSyncClient : ISyncClient
    {
        public List<PullRequest> PullCalls { get; } = [];
        public Queue<PullResponse> PullResponses { get; set; } = [];

        public Task<PullResponse> PullAsync(
            Uri serverBaseUrl, string accessToken, PullRequest request, CancellationToken cancellationToken = default)
        {
            PullCalls.Add(request);
            return Task.FromResult(PullResponses.Count > 0
                ? PullResponses.Dequeue()
                : new PullResponse(request.Domain, [], null, false, DateTimeOffset.UtcNow));
        }

        public Task<PushResponse> PushAsync(
            Uri serverBaseUrl, string accessToken, PushRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Grants sync never pushes (ADR 0031).");

        public Task<ResolveSyncConflictResponse> ResolveConflictAsync(
            Uri serverBaseUrl, string accessToken, ResolveSyncConflictRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class InMemorySyncCursorRepository : ISyncCursorRepository
    {
        private readonly Dictionary<string, string?> _cursors = [];

        public ValueTask<string?> GetCursorAsync(string domain, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_cursors.GetValueOrDefault(domain));

        public ValueTask SetCursorAsync(string domain, string? cursor, CancellationToken cancellationToken = default)
        {
            _cursors[domain] = cursor;
            return ValueTask.CompletedTask;
        }
    }
}
