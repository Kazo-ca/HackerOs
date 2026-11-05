using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Intents;
using Kazo.Os.Platform.Core.ServerConnection;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Server.Contracts.Sync;
using Kazo.Os.Simulation.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.ServerConnection;
using Kazo.Os.Simulation.Abstractions.Sync;

namespace Kazo.Os.Platform.Core.Tests.ServerConnection;

/// <summary>
/// Exercises <see cref="FileAssociationsSyncService"/> (ADR 0033) against fakes for every
/// dependency — covering the one administrator file
/// (<c>/etc/kazoos/file-associations.json</c>, ADR 0044)
/// this adapter handles and the distinct <see cref="SyncDomain.FileAssociations"/> domain string.
/// </summary>
public sealed class FileAssociationsSyncServiceTests
{
    private static readonly Uri ServerBaseUrl = new("https://example.test/");
    private static readonly ServerConnectionState Connected = new(
        Guid.NewGuid(), Guid.NewGuid(), ServerBaseUrl.ToString(), "fingerprint", "refresh-token", DateTimeOffset.UtcNow.AddDays(30));

    [Fact]
    public async Task PushAsync_WhenDisconnected_NeverCallsSyncClient()
    {
        var settings = new FakeSettingsDocumentService();
        var syncClient = new FakeSyncClient();
        var service = CreateService(settings, syncClient, connectionState: null);

        await service.PushAsync();

        Assert.Empty(syncClient.PushCalls);
    }

    [Fact]
    public async Task PushAsync_FirstPush_SendsRevisionOneUnderFileAssociationsDomain()
    {
        var settings = new FakeSettingsDocumentService();
        settings.Seed("{\"associations\":[]}");
        var syncClient = new FakeSyncClient();
        var service = CreateService(settings, syncClient, Connected);

        await service.PushAsync();

        SyncRecordEnvelope envelope = Assert.Single(Assert.Single(syncClient.PushCalls).Records);
        Assert.Equal(SyncDomain.FileAssociations, envelope.Domain);
        Assert.Equal(1, envelope.Revision);
        Assert.Equal("{\"associations\":[]}", envelope.PayloadJson);
    }

    [Fact]
    public async Task PushAsync_UnchangedSinceLastSync_SendsNothing()
    {
        var settings = new FakeSettingsDocumentService();
        settings.Seed("{\"associations\":[]}");
        var syncClient = new FakeSyncClient();
        var service = CreateService(settings, syncClient, Connected);

        await service.PushAsync();
        await service.PushAsync();

        Assert.Single(syncClient.PushCalls);
    }

    [Fact]
    public async Task PushAsync_SameServerConnection_AlwaysProducesTheSameRecordId()
    {
        var settings = new FakeSettingsDocumentService();
        settings.Seed("{\"associations\":[]}");
        var syncClient = new FakeSyncClient();
        var service = CreateService(settings, syncClient, Connected);
        await service.PushAsync();
        Guid firstRecordId = syncClient.PushCalls[0].Records[0].RecordId;

        var secondSettings = new FakeSettingsDocumentService();
        secondSettings.Seed("{\"associations\":[]}");
        var secondSyncClient = new FakeSyncClient();
        var secondService = CreateService(secondSettings, secondSyncClient, Connected);
        await secondService.PushAsync();

        Assert.Equal(firstRecordId, secondSyncClient.PushCalls[0].Records[0].RecordId);
    }

    [Fact]
    public async Task PullAsync_AppliesEnvelope_AndAdvancesCursorOnlyWhenGiven()
    {
        var settings = new FakeSettingsDocumentService();
        settings.Seed("{\"associations\":[]}");
        Guid recordId = ComputeExpectedRecordId();
        var syncClient = new FakeSyncClient
        {
            PullResponses = new Queue<PullResponse>(
            [
                new PullResponse(
                    SyncDomain.FileAssociations,
                    [new SyncRecordEnvelope(recordId, SyncDomain.FileAssociations, 1, Connected.AccountId, Connected.DeviceId,
                        5, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "hash", false, "{\"schemaVersion\":1,\"associations\":[{\"extension\":\".txt\",\"appId\":\"org.kazoos.texteditor\",\"actions\":[\"open\"]}]}")],
                    NextCursor: null,
                    HasMore: false,
                    DateTimeOffset.UtcNow)
            ])
        };
        var cursorRepository = new InMemorySyncCursorRepository();
        var service = CreateService(settings, syncClient, Connected, cursorRepository);

        await service.PullAsync();

        Assert.Equal("{\"schemaVersion\":1,\"associations\":[{\"extension\":\".txt\",\"appId\":\"org.kazoos.texteditor\",\"actions\":[\"open\"]}]}", settings.CurrentContent());
        Assert.Null(await cursorRepository.GetCursorAsync(SyncDomain.FileAssociations));
    }

    [Fact]
    public async Task PullAsync_NeverWritesAnInvalidRemoteDocumentOverTheLocalFile()
    {
        var settings = new FakeSettingsDocumentService();
        settings.Seed(FileAssociationSettingsDocuments.DefaultContent);
        var syncClient = new FakeSyncClient
        {
            PullResponses = new Queue<PullResponse>(
            [
                new PullResponse(
                    SyncDomain.FileAssociations,
                    [new SyncRecordEnvelope(ComputeExpectedRecordId(), SyncDomain.FileAssociations, 1, Connected.AccountId, Connected.DeviceId,
                        5, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "hash", false, "not json")],
                    NextCursor: null,
                    HasMore: false,
                    DateTimeOffset.UtcNow)
            ])
        };
        var service = CreateService(settings, syncClient, Connected);

        await service.PullAsync();

        Assert.Equal(FileAssociationSettingsDocuments.DefaultContent, settings.CurrentContent());
    }

    [Fact]
    public async Task PushAsync_FileDeletedAfterSync_SendsATombstoneOnce()
    {
        var settings = new FakeSettingsDocumentService();
        settings.Seed("{\"associations\":[]}");
        var syncClient = new FakeSyncClient();
        var service = CreateService(settings, syncClient, Connected);
        await service.PushAsync();

        await settings.DeleteAsync();
        await service.PushAsync();
        await service.PushAsync();

        Assert.Equal(2, syncClient.PushCalls.Count);
        SyncRecordEnvelope tombstone = Assert.Single(syncClient.PushCalls[1].Records);
        Assert.True(tombstone.IsTombstone);
        Assert.Null(tombstone.PayloadJson);
        Assert.Equal(2, tombstone.Revision);
    }

    [Fact]
    public async Task PushAsync_FileNeverSynchronized_SendsNoTombstone()
    {
        var settings = new FakeSettingsDocumentService();
        var syncClient = new FakeSyncClient();
        var service = CreateService(settings, syncClient, Connected);

        await service.PushAsync();

        Assert.Empty(syncClient.PushCalls);
    }

    [Fact]
    public async Task PullAsync_Tombstone_RestoresTheDefaultAssociations()
    {
        var settings = new FakeSettingsDocumentService();
        settings.Seed("{\"schemaVersion\":1,\"associations\":[{\"extension\":\".txt\",\"appId\":\"org.example\",\"actions\":[\"open\"]}]}");
        var syncClient = new FakeSyncClient
        {
            PullResponses = new Queue<PullResponse>(
            [
                new PullResponse(
                    SyncDomain.FileAssociations,
                    [new SyncRecordEnvelope(ComputeExpectedRecordId(), SyncDomain.FileAssociations, 1, Connected.AccountId, Guid.NewGuid(),
                        3, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "hash", IsTombstone: true, PayloadJson: null)],
                    NextCursor: "3",
                    HasMore: false,
                    DateTimeOffset.UtcNow)
            ])
        };
        var service = CreateService(settings, syncClient, Connected);

        await service.PullAsync();
        await service.PushAsync();

        Assert.Equal(FileAssociationSettingsDocuments.DefaultContent, settings.CurrentContent());
        Assert.Empty(syncClient.PushCalls); // The restored defaults are not sent back.
    }

    [Fact]
    public async Task PullAsync_WhenDisconnected_NeverCallsSyncClient()
    {
        var settings = new FakeSettingsDocumentService();
        var syncClient = new FakeSyncClient();
        var service = CreateService(settings, syncClient, connectionState: null);

        await service.PullAsync();

        Assert.Empty(syncClient.PullCalls);
    }

    private static Guid ComputeExpectedRecordId()
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(FileAssociationSettingsDocuments.Key.ToString()));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static FileAssociationsSyncService CreateService(
        FakeSettingsDocumentService settings,
        FakeSyncClient syncClient,
        ServerConnectionState? connectionState,
        InMemorySyncCursorRepository? cursorRepository = null) =>
        new(
            settings.Service,
            new FakeServerConnectionService(connectionState),
            syncClient,
            cursorRepository ?? new InMemorySyncCursorRepository(),
            new InMemorySyncRecordStateRepository());

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
        public List<PushRequest> PushCalls { get; } = [];
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
            Uri serverBaseUrl, string accessToken, PushRequest request, CancellationToken cancellationToken = default)
        {
            PushCalls.Add(request);
            return Task.FromResult(new PushResponse(PushOutcome.Accepted, request.Records.Count, []));
        }

        public Task<ResolveSyncConflictResponse> ResolveConflictAsync(
            Uri serverBaseUrl, string accessToken, ResolveSyncConflictRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    /// <summary>Real OS settings service over an in-memory filesystem, seeded as root.</summary>
    private sealed class FakeSettingsDocumentService
    {
        private readonly SettingsFilesHarness _harness = new();

        internal IOsSettingsService Service => _harness.Settings;

        public void Seed(string content) =>
            _harness.WriteAsRootAsync(Path, content).GetAwaiter().GetResult();

        public string? CurrentContent() => _harness.ReadAsync(Path).GetAwaiter().GetResult();

        /// <summary>Deletes the file, like an administrator running <c>rm</c>.</summary>
        public async Task DeleteAsync()
        {
            FileSystemAuthorizationContext root = new(
                new AppOperationContext
                {
                    AppId = "org.kazoos.shell",
                    UserId = "system",
                    UserAuthority = AppAuthority.System,
                    GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
                    IsSystemOperation = true
                },
                ["system"],
                DateTimeOffset.UtcNow);
            await new SyncFileOperations(_harness.FileSystem, root).DeleteRecursiveAsync(VirtualPath.Parse(Path), CancellationToken.None);
        }

        private static string Path => FileAssociationSettingsDocuments.File.Resolve(null).Value;
    }
}
