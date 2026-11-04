using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.FileSystem;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.ServerConnection;
using Kazo.Os.Server.Contracts.Sync;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.ServerConnection;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Sync;

namespace Kazo.Os.Platform.Core.Tests.ServerConnection;

/// <summary>
/// Exercises <see cref="FileSystemSyncService"/> (ADR 0030) against a real, in-memory-backed
/// <see cref="IFileSystemService"/> (the same fixture shape <c>FileSystemSeederTests</c> uses) and
/// fakes for the server-facing dependencies — mirrors <c>SettingsSyncServiceTests</c>'s approach.
/// </summary>
public sealed class FileSystemSyncServiceTests
{
    private static readonly Uri ServerBaseUrl = new("https://example.test/");
    private static readonly ServerConnectionState Connected = new(
        Guid.NewGuid(), Guid.NewGuid(), ServerBaseUrl.ToString(), "fingerprint", "refresh-token", DateTimeOffset.UtcNow.AddDays(30));

    [Fact]
    public async Task PushAsync_WhenNoSession_NeverCallsSyncClient()
    {
        Fixture fixture = new();
        var syncClient = new FakeSyncClient();
        var service = fixture.CreateService(principal: null, Connected, syncClient);

        await service.PushAsync();

        Assert.Empty(syncClient.PushCalls);
    }

    [Fact]
    public async Task PushAsync_WhenDisconnected_NeverCallsSyncClient()
    {
        Fixture fixture = new();
        var syncClient = new FakeSyncClient();
        var service = fixture.CreateService(fixture.Alice, connectionState: null, syncClient);

        await service.PushAsync();

        Assert.Empty(syncClient.PushCalls);
    }

    [Fact]
    public async Task PushAsync_NewFile_PushesMetadataAndUploadsContent()
    {
        Fixture fixture = new();
        await fixture.CreateFileAsync("/home/alice/note.txt", "hello world"u8.ToArray());
        var syncClient = new FakeSyncClient();
        var contentClient = new FakeContentTransferClient();
        var service = fixture.CreateService(fixture.Alice, Connected, syncClient, contentClient);

        await service.PushAsync();

        // The clean-profile seeder also creates several standard directories (Desktop, Documents,
        // .config, ...) under the home root — those sync too, alongside the file this test cares about.
        List<SyncRecordEnvelope> records = [.. Assert.Single(syncClient.PushCalls).Records];
        SyncRecordEnvelope envelope = Assert.Single(records, candidate =>
        {
            FileSystemSyncPayload candidatePayload = System.Text.Json.JsonSerializer.Deserialize(
                candidate.PayloadJson!, FileSystemSyncContractsJsonContext.Default.FileSystemSyncPayload)!;
            return candidatePayload.RelativePath == "note.txt";
        });
        Assert.Equal(SyncDomain.FileSystem, envelope.Domain);
        Assert.Equal(1, envelope.Revision);

        FileSystemSyncPayload payload = System.Text.Json.JsonSerializer.Deserialize(
            envelope.PayloadJson!, FileSystemSyncContractsJsonContext.Default.FileSystemSyncPayload)!;
        Assert.Equal(nameof(FileSystemEntryKind.File), payload.Kind);
        Assert.NotNull(payload.ContentHash);

        var uploadCall = Assert.Single(
            contentClient.UploadChunkCalls, call => call.SessionContentHash == payload.ContentHash);
        Assert.Equal("hello world"u8.ToArray(), uploadCall.Data);
    }

    [Fact]
    public async Task PushAsync_UnchangedSinceLastSync_SendsNothing()
    {
        Fixture fixture = new();
        await fixture.CreateFileAsync("/home/alice/note.txt", "hello world"u8.ToArray());
        var syncClient = new FakeSyncClient();
        var service = fixture.CreateService(fixture.Alice, Connected, syncClient);

        await service.PushAsync();
        await service.PushAsync(); // Nothing changed locally between calls.

        Assert.Single(syncClient.PushCalls);
    }

    [Fact]
    public async Task PushAsync_ExistingContentHash_SkipsChunkUpload()
    {
        Fixture fixture = new();
        await fixture.CreateFileAsync("/home/alice/note.txt", "hello world"u8.ToArray());
        var syncClient = new FakeSyncClient();
        var contentClient = new FakeContentTransferClient { AlwaysAlreadyExists = true };
        var service = fixture.CreateService(fixture.Alice, Connected, syncClient, contentClient);

        await service.PushAsync();

        Assert.Single(syncClient.PushCalls); // Metadata still pushes...
        Assert.Empty(contentClient.UploadChunkCalls); // ...but no bytes transfer (dedup short-circuit).
    }

    [Fact]
    public async Task PushAsync_ServerReportsConflict_SkipsRecordStateAndSurfacesRelativePath()
    {
        Fixture fixture = new();
        await fixture.CreateFileAsync("/home/alice/note.txt", "hello world"u8.ToArray());
        var syncClient = new FakeSyncClient { ConflictEveryRecord = true };
        var recordState = new InMemorySyncRecordStateRepository();
        var service = fixture.CreateService(fixture.Alice, Connected, syncClient, recordState: recordState);

        await service.PushAsync();

        // Every pushed record conflicts (the fake reports a conflict for all of them), including the
        // seeder's standard directories alongside the file this test cares about.
        Assert.Contains(await service.GetConflictsAsync(), conflict => conflict.RelativePath == "note.txt");
        SyncRecordEnvelope envelope = Assert.Single(syncClient.PushCalls[0].Records, r =>
        {
            FileSystemSyncPayload payload = System.Text.Json.JsonSerializer.Deserialize(
                r.PayloadJson!, FileSystemSyncContractsJsonContext.Default.FileSystemSyncPayload)!;
            return payload.RelativePath == "note.txt";
        });
        Assert.Null(await recordState.GetAsync(SyncDomain.FileSystem, envelope.RecordId));

        // ADR 0101 section 2: a conflicted file is frozen until the user chooses, so the next push
        // neither resends it nor drops the conflict.
        syncClient.ConflictEveryRecord = false;
        await service.PushAsync();
        Assert.DoesNotContain(syncClient.PushCalls.Skip(1).SelectMany(call => call.Records), r => r.RecordId == envelope.RecordId);
        Assert.Contains(await service.GetConflictsAsync(), conflict => conflict.RelativePath == "note.txt");
    }

    [Fact]
    public async Task PullAsync_RecordConflictedByEarlierPush_IsNotAppliedOverLocalEdit()
    {
        Fixture fixture = new();
        await fixture.CreateFileAsync("/home/alice/note.txt", "local edit"u8.ToArray());
        var syncClient = new FakeSyncClient { ConflictEveryRecord = true };
        var contentClient = new FakeContentTransferClient();
        var recordState = new InMemorySyncRecordStateRepository();
        var service = fixture.CreateService(fixture.Alice, Connected, syncClient, contentClient, recordState);

        // This push conflicts (server has a concurrently-changed copy) — per ADR 0030 Decision 5,
        // neither copy should be applied, and the record is flagged for the pull step below.
        await service.PushAsync();
        SyncRecordEnvelope pushedEnvelope = Assert.Single(syncClient.PushCalls[0].Records, r =>
        {
            FileSystemSyncPayload payload = System.Text.Json.JsonSerializer.Deserialize(
                r.PayloadJson!, FileSystemSyncContractsJsonContext.Default.FileSystemSyncPayload)!;
            return payload.RelativePath == "note.txt";
        });

        // Simulate the server handing back "its" version of the very record that just conflicted.
        byte[] serverContent = "server version"u8.ToArray();
        string serverContentHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(serverContent));
        contentClient.SeedDownload(serverContentHash, serverContent);
        var serverPayload = new FileSystemSyncPayload(
            "note.txt", nameof(FileSystemEntryKind.File), "alice", "alice-group", 0x1A4,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, serverContentHash, serverContent.Length, null);
        syncClient.PullResponses = new Queue<PullResponse>(
        [
            new PullResponse(
                SyncDomain.FileSystem,
                [new SyncRecordEnvelope(pushedEnvelope.RecordId, SyncDomain.FileSystem, 1, Connected.AccountId, Connected.DeviceId,
                    99, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "hash", false,
                    System.Text.Json.JsonSerializer.Serialize(serverPayload, FileSystemSyncContractsJsonContext.Default.FileSystemSyncPayload))],
                NextCursor: null,
                HasMore: false,
                DateTimeOffset.UtcNow)
        ]);

        await service.PullAsync();

        // The conflicted record must not be silently overwritten by whatever the server pulls back.
        byte[] localContent = await fixture.ReadFileAsync("/home/alice/note.txt");
        Assert.Equal("local edit"u8.ToArray(), localContent);
        Assert.Null(await recordState.GetAsync(SyncDomain.FileSystem, pushedEnvelope.RecordId));
    }

    [Fact]
    public async Task PullAsync_NewFile_CreatesLocalFileWithDownloadedContent()
    {
        Fixture fixture = new();
        byte[] content = "pulled content"u8.ToArray();
        string contentHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));
        var payload = new FileSystemSyncPayload(
            "downloaded.txt", nameof(FileSystemEntryKind.File), "alice", "alice-group", 0x1A4,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, contentHash, content.Length, null);
        string payloadJson = System.Text.Json.JsonSerializer.Serialize(
            payload, FileSystemSyncContractsJsonContext.Default.FileSystemSyncPayload);

        var syncClient = new FakeSyncClient
        {
            PullResponses = new Queue<PullResponse>(
            [
                new PullResponse(
                    SyncDomain.FileSystem,
                    [new SyncRecordEnvelope(Guid.NewGuid(), SyncDomain.FileSystem, 1, Connected.AccountId, Connected.DeviceId,
                        1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "hash", false, payloadJson)],
                    NextCursor: null,
                    HasMore: false,
                    DateTimeOffset.UtcNow)
            ])
        };
        var contentClient = new FakeContentTransferClient();
        contentClient.SeedDownload(contentHash, content);
        var service = fixture.CreateService(fixture.Alice, Connected, syncClient, contentClient);

        await service.PullAsync();

        byte[] written = await fixture.ReadFileAsync("/home/alice/downloaded.txt");
        Assert.Equal(content, written);
    }

    [Fact]
    public async Task PullAsync_DirectoryAndNestedFileInSamePage_DirectoryAppliedFirst()
    {
        Fixture fixture = new();
        byte[] content = "nested"u8.ToArray();
        string contentHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));

        var filePayload = new FileSystemSyncPayload(
            "Reports/q1.txt", nameof(FileSystemEntryKind.File), "alice", "alice-group", 0x1A4,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, contentHash, content.Length, null);
        var dirPayload = new FileSystemSyncPayload(
            "Reports", nameof(FileSystemEntryKind.Directory), "alice", "alice-group", 0x1C0,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null);

        // Deliberately listed file-before-directory to prove the service reorders by depth itself.
        var syncClient = new FakeSyncClient
        {
            PullResponses = new Queue<PullResponse>(
            [
                new PullResponse(
                    SyncDomain.FileSystem,
                    [
                        Envelope(filePayload),
                        Envelope(dirPayload)
                    ],
                    NextCursor: null,
                    HasMore: false,
                    DateTimeOffset.UtcNow)
            ])
        };
        var contentClient = new FakeContentTransferClient();
        contentClient.SeedDownload(contentHash, content);
        var service = fixture.CreateService(fixture.Alice, Connected, syncClient, contentClient);

        await service.PullAsync();

        byte[] written = await fixture.ReadFileAsync("/home/alice/Reports/q1.txt");
        Assert.Equal(content, written);

        static SyncRecordEnvelope Envelope(FileSystemSyncPayload payload) => new(
            Guid.NewGuid(), SyncDomain.FileSystem, 1, Connected.AccountId, Connected.DeviceId,
            1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "hash", false,
            System.Text.Json.JsonSerializer.Serialize(payload, FileSystemSyncContractsJsonContext.Default.FileSystemSyncPayload));
    }

    [Fact]
    public async Task PullAsync_WhenDisconnected_NeverCallsSyncClient()
    {
        Fixture fixture = new();
        var syncClient = new FakeSyncClient();
        var service = fixture.CreateService(fixture.Alice, connectionState: null, syncClient);

        await service.PullAsync();

        Assert.Empty(syncClient.PullCalls);
    }

    private sealed class Fixture
    {
        private int _entryId = 1;
        private int _transactionId = 100;
        private readonly FileSystemService _service;

        internal Fixture()
        {
            InMemoryFileSystemRepository repository = new(
                () => FileSystemEntryId.FromGuid(new Guid(_entryId++, 0, 0, new byte[8])),
                () => new Guid(_transactionId++, 0, 0, new byte[8]),
                TimeProvider.System);
            FileSystemMountRouter router = new(repository);
            _service = new FileSystemService(
                router,
                new FileSystemPathResolver(router),
                new FileSystemAuthorizer(),
                new InMemoryTopicMessageBus(new CapabilityGrantRepository()),
                () => new Guid(_transactionId++, 0, 0, new byte[8]));

            AliceGroupId = LocalGroupId.FromGuid(Guid.NewGuid());
            Alice = new AuthenticatedPrincipal(
                SessionId.FromGuid(Guid.NewGuid()),
                LocalUserId.FromGuid(Guid.NewGuid()),
                LocalLoginName.Parse("alice"),
                "Alice",
                AppAuthority.User,
                AliceGroupId,
                [AliceGroupId],
                InstallationId.FromGuid(Guid.NewGuid()),
                DeviceId.FromGuid(Guid.NewGuid()),
                DateTimeOffset.UtcNow);

            new FileSystemSeeder(_service).SeedAsync("alice", AliceGroupId.ToString()).AsTask().GetAwaiter().GetResult();
        }

        internal AuthenticatedPrincipal Alice { get; }

        internal LocalGroupId AliceGroupId { get; }

        internal FileSystemSyncService CreateService(
            AuthenticatedPrincipal? principal,
            ServerConnectionState? connectionState,
            FakeSyncClient syncClient,
            FakeContentTransferClient? contentClient = null,
            InMemorySyncRecordStateRepository? recordState = null) =>
            new(
                _service,
                new FakeSessionService(principal),
                new FakeServerConnectionService(connectionState),
                syncClient,
                contentClient ?? new FakeContentTransferClient(),
                new InMemorySyncCursorRepository(),
                recordState ?? new InMemorySyncRecordStateRepository());

        internal async Task CreateFileAsync(string path, byte[] content)
        {
            FileSystemAuthorizationContext context = UserContext();
            VirtualPath virtualPath = VirtualPath.Parse(path);
            FileSystemMutationResult created = await _service.CreateAsync(
                new FileSystemCreateRequest(virtualPath, FileSystemEntryKind.File, FileSystemPermissions.FromMode(0x1A4)),
                context);
            if (!created.Succeeded)
            {
                throw new InvalidOperationException($"Setup failed to create '{path}': {created.Transaction.Error?.Code}.");
            }

            FileSystemMutationResult written = await _service.WriteAsync(
                new FileSystemWriteRequest(virtualPath), new ByteContentSource(content), context);
            if (!written.Succeeded)
            {
                throw new InvalidOperationException($"Setup failed to write '{path}': {written.Transaction.Error?.Code}.");
            }
        }

        internal async Task<byte[]> ReadFileAsync(string path)
        {
            FileSystemResult<FileSystemContentReadHandle> result = await _service.ReadAsync(
                new FileSystemReadRequest(VirtualPath.Parse(path)), UserContext());
            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"Read failed for '{path}': {result.Error?.Code}.");
            }

            await using FileSystemContentReadHandle handle = result.Value!;
            using MemoryStream buffer = new();
            await handle.Content.CopyToAsync(buffer);
            return buffer.ToArray();
        }

        private FileSystemAuthorizationContext UserContext() => new(
            new AppOperationContext
            {
                AppId = "test",
                UserId = Alice.LoginName.Value,
                UserAuthority = Alice.Authority,
                GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
                IsSystemOperation = false
            },
            Alice.GroupIds.Select(group => group.ToString()),
            DateTimeOffset.UtcNow);

        private sealed class ByteContentSource(byte[] bytes) : IFileSystemContentSource
        {
            public FileSystemContentDescriptor Descriptor { get; } = FileSystemContentDescriptor.Binary();
            public long? Length => bytes.Length;
            public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
                ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
    }

    private sealed class FakeSessionService(AuthenticatedPrincipal? principal) : ISessionService
    {
        public SessionState State => principal is null ? SessionState.LoggedOut : SessionState.Active;
        public AuthenticatedPrincipal? CurrentPrincipal => principal;

        public Task<AuthenticatedPrincipal> LoginAsync(
            LocalLoginName loginName, string? password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task LogoutAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public CancellationTokenSource CreateLinkedCancellationSource() => throw new NotSupportedException();
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
        public List<PushRequest> PushCalls { get; } = [];
        public List<PullRequest> PullCalls { get; } = [];
        public Queue<PullResponse> PullResponses { get; set; } = [];
        public bool ConflictEveryRecord { get; set; }

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
            if (ConflictEveryRecord)
            {
                var conflicts = request.Records
                    .Select(r => new SyncConflict(r.RecordId, r.Revision, -1, DateTimeOffset.UtcNow, "CONCURRENT_EDIT"))
                    .ToList();
                return Task.FromResult(new PushResponse(PushOutcome.ConflictsDetected, 0, conflicts));
            }

            return Task.FromResult(new PushResponse(PushOutcome.Accepted, request.Records.Count, []));
        }

        public Task<ResolveSyncConflictResponse> ResolveConflictAsync(
            Uri serverBaseUrl, string accessToken, ResolveSyncConflictRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeContentTransferClient : IContentTransferClient
    {
        private const int ChunkSizeBytes = 256 * 1024;
        private readonly Dictionary<string, byte[]> _downloadableByHash = [];

        public List<(string SessionContentHash, int ChunkIndex, byte[] Data)> UploadChunkCalls { get; } = [];
        public bool AlwaysAlreadyExists { get; set; }

        public void SeedDownload(string contentHash, byte[] content) => _downloadableByHash[contentHash] = content;

        public Task<InitiateContentUploadResponse> InitiateUploadAsync(
            Uri serverBaseUrl, string accessToken, InitiateContentUploadRequest request, CancellationToken cancellationToken = default)
        {
            int totalChunks = AlwaysAlreadyExists ? 0 : (int)Math.Ceiling((double)request.TotalBytes / ChunkSizeBytes);
            return Task.FromResult(new InitiateContentUploadResponse(
                request.ContentHash, request.ContentHash, AlwaysAlreadyExists, ChunkSizeBytes, totalChunks, DateTimeOffset.UtcNow.AddHours(1)));
        }

        public Task<QueryUploadProgressResponse> QueryUploadProgressAsync(
            Uri serverBaseUrl, string accessToken, string uploadSessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task UploadChunkAsync(
            Uri serverBaseUrl, string accessToken, string uploadSessionId, int chunkIndex, byte[] chunkData, CancellationToken cancellationToken = default)
        {
            UploadChunkCalls.Add((uploadSessionId, chunkIndex, chunkData));
            return Task.CompletedTask;
        }

        public Task<InitiateContentDownloadResponse> InitiateDownloadAsync(
            Uri serverBaseUrl, string accessToken, InitiateContentDownloadRequest request, CancellationToken cancellationToken = default)
        {
            byte[] content = _downloadableByHash[request.ContentHash];
            return Task.FromResult(new InitiateContentDownloadResponse(
                request.ContentHash, request.ContentHash, content.Length, content.Length, ChunkSizeBytes, DateTimeOffset.UtcNow.AddHours(1)));
        }

        public Task<byte[]> DownloadChunkAsync(
            Uri serverBaseUrl, string accessToken, string contentHash, int chunkIndex, CancellationToken cancellationToken = default)
        {
            byte[] content = _downloadableByHash[contentHash];
            int start = chunkIndex * ChunkSizeBytes;
            int length = Math.Min(ChunkSizeBytes, content.Length - start);
            return Task.FromResult(content[start..(start + length)]);
        }
    }
}
