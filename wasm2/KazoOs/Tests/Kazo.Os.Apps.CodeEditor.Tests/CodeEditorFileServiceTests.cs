using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Xunit;

namespace Kazo.Os.Apps.CodeEditor.Tests;

public sealed class CodeEditorFileServiceTests
{
    [Fact]
    public async Task Existing_file_round_trip_uses_loaded_revision_and_clears_dirty_state()
    {
        TestFileSystemGateway gateway = new();
        gateway.AddText("/home/user/app.cs", "class Before {}", revision: 7);
        CodeEditorFileService service = new(gateway);
        VirtualPath path = VirtualPath.Parse("/home/user/app.cs");

        CodeEditorLoadResult loaded = await service.LoadAsync(path);
        CodeEditorDocument document = CodeEditorDocument.CreateLoaded(
            path, loaded.Content!, loaded.Revision!.Value);
        Assert.True(document.TryEdit("class After {}").Succeeded);
        CodeEditorSaveResult saved = await service.SaveAsync(document, path);

        Assert.True(saved.Succeeded);
        Assert.Equal(7, gateway.LastExpectedWriteRevision);
        Assert.Equal("class After {}", gateway.GetText(path.Value));
        Assert.False(document.IsDirty);
        Assert.Equal(saved.Revision, document.Revision);
    }

    [Fact]
    public async Task Save_as_creates_new_file_and_writes_content_atomically()
    {
        TestFileSystemGateway gateway = new();
        CodeEditorFileService service = new(gateway);
        CodeEditorDocument document = CodeEditorDocument.CreateNew(1);
        document.SetSyntaxMode(CodeEditorSyntaxMode.Json);
        Assert.True(document.TryEdit("{\"safe\":true}").Succeeded);
        VirtualPath path = VirtualPath.Parse("/home/user/config.json");

        CodeEditorSaveResult result = await service.SaveAsync(document, path);

        Assert.True(result.Succeeded);
        Assert.Equal(path.Value, gateway.CreatedPath);
        Assert.Equal("{\"safe\":true}", gateway.GetText(path.Value));
        Assert.Equal("application/json", gateway.LastDescriptor?.MediaType);
        Assert.Equal(path, document.Path);
        Assert.False(document.IsDirty);
    }

    [Fact]
    public async Task Capability_denial_is_typed_and_does_not_create_a_document()
    {
        TestFileSystemGateway gateway = new() { DenyReads = true };
        CodeEditorFileService service = new(gateway);

        CodeEditorLoadResult result = await service.LoadAsync(
            VirtualPath.Parse("/home/user/private.cs"));

        Assert.Equal(CodeEditorFileStatus.Denied, result.Status);
        Assert.False(result.Succeeded);
        Assert.Null(result.Content);
    }

    [Fact]
    public async Task Binary_and_large_files_are_rejected_before_editor_allocation()
    {
        TestFileSystemGateway binaryGateway = new();
        binaryGateway.AddBinary("/home/user/image.bin", [1, 2, 3], revision: 1);
        CodeEditorLoadResult binary = await new CodeEditorFileService(binaryGateway).LoadAsync(
            VirtualPath.Parse("/home/user/image.bin"));

        TestFileSystemGateway largeGateway = new();
        largeGateway.AddText(
            "/home/user/large.txt",
            new string('x', CodeEditorDocument.MaxDocumentBytes + 1),
            revision: 1);
        CodeEditorLoadResult large = await new CodeEditorFileService(largeGateway).LoadAsync(
            VirtualPath.Parse("/home/user/large.txt"));

        Assert.Equal(CodeEditorFileStatus.UnsupportedContent, binary.Status);
        Assert.Equal(CodeEditorFileStatus.TooLarge, large.Status);
    }

    [Fact]
    public async Task Revision_conflict_keeps_dirty_buffer_for_recovery()
    {
        TestFileSystemGateway gateway = new() { RejectWritesWithConflict = true };
        gateway.AddText("/home/user/app.js", "old", revision: 3);
        CodeEditorDocument document = CodeEditorDocument.CreateLoaded(
            VirtualPath.Parse("/home/user/app.js"), "old", 3);
        Assert.True(document.TryEdit("new").Succeeded);

        CodeEditorSaveResult result = await new CodeEditorFileService(gateway).SaveAsync(
            document, VirtualPath.Parse("/home/user/app.js"));

        Assert.Equal(CodeEditorFileStatus.Conflict, result.Status);
        Assert.True(document.IsDirty);
        Assert.Equal("new", document.Content);
        Assert.Equal(3, document.Revision);
    }

    [Fact]
    public async Task Cancelled_save_is_not_a_failure_and_keeps_the_buffer_dirty()
    {
        TestFileSystemGateway gateway = new() { CancelWrites = true };
        gateway.AddText("/home/user/app.js", "old", revision: 3);
        CodeEditorDocument document = CodeEditorDocument.CreateLoaded(
            VirtualPath.Parse("/home/user/app.js"), "old", 3);
        Assert.True(document.TryEdit("new").Succeeded);

        CodeEditorSaveResult result = await new CodeEditorFileService(gateway).SaveAsync(
            document, VirtualPath.Parse("/home/user/app.js"));

        Assert.Equal(CodeEditorFileStatus.Cancelled, result.Status);
        Assert.Null(result.Error);
        Assert.True(document.IsDirty);
    }

    private sealed class TestFileSystemGateway : IAppFileSystemGateway
    {
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

        public bool DenyReads { get; init; }
        public bool RejectWritesWithConflict { get; init; }
        public bool CancelWrites { get; init; }
        public string? CreatedPath { get; private set; }
        public long? LastExpectedWriteRevision { get; private set; }
        public FileSystemContentDescriptor? LastDescriptor { get; private set; }

        public void AddText(string path, string content, long revision) =>
            _entries[path] = new Entry(Encoding.UTF8.GetBytes(content), revision, false);

        public void AddBinary(string path, byte[] content, long revision) =>
            _entries[path] = new Entry(content, revision, true);

        public string GetText(string path) => Encoding.UTF8.GetString(_entries[path].Content);

        public ValueTask<FileSystemResult<FileSystemContentReadHandle>> ReadAsync(
            FileSystemReadRequest request, CancellationToken cancellationToken = default)
        {
            if (DenyReads)
            {
                return ValueTask.FromResult(FileSystemResult<FileSystemContentReadHandle>.Failure(
                    Error(FileSystemOperation.Read, FileSystemErrorCode.CapabilityDenied, request.Path)));
            }

            if (!_entries.TryGetValue(request.Path.Value, out Entry? entry))
            {
                return ValueTask.FromResult(FileSystemResult<FileSystemContentReadHandle>.Failure(
                    Error(FileSystemOperation.Read, FileSystemErrorCode.NotFound, request.Path)));
            }

            FileSystemContentDescriptor descriptor = entry.IsBinary
                ? FileSystemContentDescriptor.Binary()
                : FileSystemContentDescriptor.Text();
            return ValueTask.FromResult(FileSystemResult<FileSystemContentReadHandle>.Success(
                new FileSystemContentReadHandle(
                    FileSnapshot(request.Path, entry),
                    descriptor,
                    new MemoryStream(entry.Content, writable: false))));
        }

        public async ValueTask<FileSystemMutationResult> WriteAsync(
            FileSystemWriteRequest request,
            IFileSystemContentSource content,
            CancellationToken cancellationToken = default)
        {
            LastExpectedWriteRevision = request.ExpectedRevision;
            LastDescriptor = content.Descriptor;
            if (CancelWrites)
            {
                return new FileSystemMutationResult(FileSystemTransactionResult.Cancelled(
                    Guid.NewGuid(), Error(FileSystemOperation.Write, FileSystemErrorCode.Cancelled, request.Path)));
            }

            if (RejectWritesWithConflict)
            {
                return Rejected(
                    FileSystemOperation.Write, FileSystemErrorCode.RevisionConflict, request.Path);
            }

            await using Stream stream = await content.OpenReadAsync(cancellationToken);
            using MemoryStream copy = new();
            await stream.CopyToAsync(copy, cancellationToken);
            long revision = (request.ExpectedRevision ?? 0) + 1;
            Entry entry = new(copy.ToArray(), revision, false);
            _entries[request.Path.Value] = entry;
            FileSystemEntrySnapshot snapshot = FileSnapshot(request.Path, entry);
            return new FileSystemMutationResult(
                FileSystemTransactionResult.Committed(Guid.NewGuid(), [snapshot.Metadata.Id]),
                snapshot);
        }

        public ValueTask<FileSystemMutationResult> CreateAsync(
            FileSystemCreateRequest request, CancellationToken cancellationToken = default)
        {
            CreatedPath = request.Path.Value;
            Entry entry = new([], 1, false);
            _entries.Add(request.Path.Value, entry);
            FileSystemEntrySnapshot snapshot = FileSnapshot(request.Path, entry);
            return ValueTask.FromResult(new FileSystemMutationResult(
                FileSystemTransactionResult.Committed(Guid.NewGuid(), [snapshot.Metadata.Id]),
                snapshot));
        }

        public ValueTask<FileSystemResult<FileSystemEntrySnapshot>> StatAsync(
            FileSystemStatRequest request, CancellationToken cancellationToken = default)
        {
            if (_entries.TryGetValue(request.Path.Value, out Entry? entry))
            {
                return ValueTask.FromResult(FileSystemResult<FileSystemEntrySnapshot>.Success(
                    FileSnapshot(request.Path, entry)));
            }

            if (request.Path.Value is "/" or "/home" or "/home/user")
            {
                return ValueTask.FromResult(FileSystemResult<FileSystemEntrySnapshot>.Success(
                    DirectorySnapshot(request.Path, 1)));
            }

            return ValueTask.FromResult(FileSystemResult<FileSystemEntrySnapshot>.Failure(
                Error(FileSystemOperation.Stat, FileSystemErrorCode.NotFound, request.Path)));
        }

        public ValueTask<FileSystemResult<FileSystemDirectorySnapshot>> EnumerateAsync(
            FileSystemEnumerateRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<FileSystemMutationResult> MoveAsync(
            FileSystemMoveRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<FileSystemMutationResult> CopyAsync(
            FileSystemCopyRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<FileSystemMutationResult> DeleteAsync(
            FileSystemDeleteRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<FileSystemMutationResult> SetPermissionsAsync(
            FileSystemSetPermissionsRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAppFileSystemGateway WithSelectedHandle(FileSystemSelectedResourceHandle handle) => this;

        private FileSystemEntrySnapshot FileSnapshot(VirtualPath path, Entry entry) => new(
            path,
            new FileMetadata(
                FileSystemEntryId.FromGuid(Guid.NewGuid()),
                "user",
                "users",
                FileSystemPermissions.FromMode(0b110_100_100),
                new FileSystemTimestamps(_now, _now, _now),
                entry.Revision,
                entry.Content.LongLength,
                entry.IsBinary ? "application/octet-stream" : "text/plain"));

        private FileSystemEntrySnapshot DirectorySnapshot(VirtualPath path, long revision) => new(
            path,
            new DirectoryMetadata(
                FileSystemEntryId.FromGuid(Guid.NewGuid()),
                "user",
                "users",
                FileSystemPermissions.FromMode(0b111_101_101),
                new FileSystemTimestamps(_now, _now, _now),
                revision));

        private static FileSystemError Error(
            FileSystemOperation operation, FileSystemErrorCode code, VirtualPath path) =>
            new(operation, code, path);

        private static FileSystemMutationResult Rejected(
            FileSystemOperation operation, FileSystemErrorCode code, VirtualPath path) =>
            new(FileSystemTransactionResult.Rejected(Guid.NewGuid(), Error(operation, code, path)));

        private sealed record Entry(byte[] Content, long Revision, bool IsBinary);
    }
}
