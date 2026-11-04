using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Xunit;

namespace Kazo.Os.Apps.CodeEditor.Tests;

public sealed class CodeEditorRecoveryStoreTests
{
    [Fact]
    public async Task Save_then_load_round_trips_dirty_tabs_and_active_selection()
    {
        MemoryFileSystemGateway gateway = new();
        CodeEditorRecoveryStore store = new(gateway, "user", "/home/user");
        CodeEditorSession session = new();
        CodeEditorDocument first = session.NewDocument();
        Assert.True(first.TryEdit("unsaved first").Succeeded);
        CodeEditorDocument second = session.OpenDocument(
            VirtualPath.Parse("/home/user/project/app.cs"), "class A {}", 4);
        Assert.True(second.TryEdit("class Changed {}").Succeeded);
        session.Activate(first.Id);

        Assert.Equal(CodeEditorRecoveryStatus.Success, (await store.SaveAsync(session.CaptureRecovery())).Status);
        CodeEditorRecoveryResult loaded = await store.LoadAsync();

        Assert.True(loaded.HasSession);
        CodeEditorSession restored = CodeEditorSession.Restore(loaded.Session!);
        Assert.Equal(first.Id, restored.ActiveDocumentId);
        Assert.Equal(2, restored.Documents.Count);
        Assert.Equal("unsaved first", restored.ActiveDocument!.Content);
        Assert.True(restored.ActiveDocument.IsDirty);
        Assert.Contains(restored.Documents, document => document.Path?.Value == "/home/user/project/app.cs" && document.IsDirty);
    }

    [Fact]
    public async Task Save_uses_the_supplied_home_path_not_the_opaque_user_id()
    {
        // AppContext.UserId is an internal identity, not the VFS folder name under /home — a
        // store built from userId alone would target a nonexistent /home/{userId} directory and
        // every save would fail with a raw "NotFound" status. Regression coverage for that bug.
        MemoryFileSystemGateway gateway = new();
        CodeEditorRecoveryStore store = new(gateway, "3f8a2b1c-guid-not-a-folder", "/home/user");
        CodeEditorSession session = new();
        session.NewDocument().TryEdit("uses real home path");

        CodeEditorRecoveryResult saved = await store.SaveAsync(session.CaptureRecovery());

        Assert.Equal(CodeEditorRecoveryStatus.Success, saved.Status);
        Assert.Equal("/home/user/.local/state/hackeros/code-editor/session.json", store.RecoveryPath.Value);
    }

    [Fact]
    public async Task Superseded_save_reports_cancelled_without_error_text()
    {
        // The editor cancels a pending snapshot on every new edit. The provider then answers
        // "Cancelled", which used to reach the error banner as the raw text "Cancelled".
        MemoryFileSystemGateway gateway = new() { CancelWrites = true };
        CodeEditorRecoveryStore store = new(gateway, "user", "/home/user");
        CodeEditorSession session = new();
        session.NewDocument().TryEdit("typing");

        CodeEditorRecoveryResult saved = await store.SaveAsync(session.CaptureRecovery());

        Assert.Equal(CodeEditorRecoveryStatus.Cancelled, saved.Status);
        Assert.Null(saved.Error);
    }

    [Fact]
    public async Task Clear_removes_only_the_app_private_recovery_file()
    {
        MemoryFileSystemGateway gateway = new();
        CodeEditorRecoveryStore store = new(gateway, "user", "/home/user");
        CodeEditorSession session = new();
        session.NewDocument().TryEdit("temporary recovery");
        await store.SaveAsync(session.CaptureRecovery());

        CodeEditorRecoveryResult cleared = await store.ClearAsync();

        Assert.Equal(CodeEditorRecoveryStatus.Success, cleared.Status);
        Assert.Equal(CodeEditorRecoveryStatus.Missing, (await store.LoadAsync()).Status);
        Assert.True(gateway.DirectoryExists("/home/user/.local/state/hackeros/code-editor"));
    }

    [Fact]
    public async Task Malformed_recovery_is_typed_invalid_and_does_not_throw()
    {
        MemoryFileSystemGateway gateway = new();
        CodeEditorRecoveryStore store = new(gateway, "user", "/home/user");
        gateway.CreateDirectoriesFor(store.RecoveryPath);
        gateway.AddText(store.RecoveryPath, "{ not valid json", revision: 1);

        CodeEditorRecoveryResult result = await store.LoadAsync();

        Assert.Equal(CodeEditorRecoveryStatus.Invalid, result.Status);
        Assert.False(result.HasSession);
    }

    [Fact]
    public async Task Vfs_denial_is_exposed_without_falling_back_to_browser_storage()
    {
        MemoryFileSystemGateway gateway = new() { DenyReads = true };
        CodeEditorRecoveryStore store = new(gateway, "user", "/home/user");

        CodeEditorRecoveryResult result = await store.LoadAsync();

        Assert.Equal(CodeEditorRecoveryStatus.Denied, result.Status);
        Assert.Empty(gateway.Files);
    }

    private sealed class MemoryFileSystemGateway : IAppFileSystemGateway
    {
        private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal)
        {
            ["/"] = Node.Directory(1),
            ["/home"] = Node.Directory(1),
            ["/home/user"] = Node.Directory(1)
        };
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

        public bool DenyReads { get; init; }
        public bool CancelWrites { get; init; }
        public IReadOnlyCollection<string> Files => _nodes.Where(item => !item.Value.IsDirectory).Select(item => item.Key).ToArray();

        public bool DirectoryExists(string path) => _nodes.TryGetValue(path, out Node? node) && node.IsDirectory;

        public void CreateDirectoriesFor(VirtualPath file)
        {
            string current = "/";
            foreach (string segment in file.Value.Split('/', StringSplitOptions.RemoveEmptyEntries)[..^1])
            {
                current = current == "/" ? $"/{segment}" : $"{current}/{segment}";
                _nodes.TryAdd(current, Node.Directory(1));
            }
        }

        public void AddText(VirtualPath path, string content, long revision) =>
            _nodes[path.Value] = Node.File(Encoding.UTF8.GetBytes(content), revision);

        public ValueTask<FileSystemResult<FileSystemContentReadHandle>> ReadAsync(
            FileSystemReadRequest request, CancellationToken cancellationToken = default)
        {
            if (DenyReads)
            {
                return ValueTask.FromResult(FileSystemResult<FileSystemContentReadHandle>.Failure(
                    Error(FileSystemOperation.Read, FileSystemErrorCode.CapabilityDenied, request.Path)));
            }

            if (!_nodes.TryGetValue(request.Path.Value, out Node? node))
            {
                return ValueTask.FromResult(FileSystemResult<FileSystemContentReadHandle>.Failure(
                    Error(FileSystemOperation.Read, FileSystemErrorCode.NotFound, request.Path)));
            }

            if (node.IsDirectory)
            {
                return ValueTask.FromResult(FileSystemResult<FileSystemContentReadHandle>.Failure(
                    Error(FileSystemOperation.Read, FileSystemErrorCode.NotFile, request.Path)));
            }

            return ValueTask.FromResult(FileSystemResult<FileSystemContentReadHandle>.Success(
                new FileSystemContentReadHandle(Snapshot(request.Path, node), FileSystemContentDescriptor.Text("application/json"),
                    new MemoryStream(node.Content!, writable: false))));
        }

        public async ValueTask<FileSystemMutationResult> WriteAsync(
            FileSystemWriteRequest request, IFileSystemContentSource content,
            CancellationToken cancellationToken = default)
        {
            if (CancelWrites)
            {
                return new FileSystemMutationResult(FileSystemTransactionResult.Cancelled(
                    Guid.NewGuid(), Error(FileSystemOperation.Write, FileSystemErrorCode.Cancelled, request.Path)));
            }

            if (!_nodes.TryGetValue(request.Path.Value, out Node? current))
            {
                return Rejected(FileSystemOperation.Write, FileSystemErrorCode.NotFound, request.Path);
            }

            if (current.Revision != request.ExpectedRevision)
            {
                return Rejected(FileSystemOperation.Write, FileSystemErrorCode.RevisionConflict, request.Path);
            }

            await using Stream source = await content.OpenReadAsync(cancellationToken);
            using MemoryStream target = new();
            await source.CopyToAsync(target, cancellationToken);
            Node updated = Node.File(target.ToArray(), current.Revision + 1);
            _nodes[request.Path.Value] = updated;
            FileSystemEntrySnapshot snapshot = Snapshot(request.Path, updated);
            return new FileSystemMutationResult(FileSystemTransactionResult.Committed(Guid.NewGuid(), [snapshot.Metadata.Id]), snapshot);
        }

        public ValueTask<FileSystemMutationResult> CreateAsync(
            FileSystemCreateRequest request, CancellationToken cancellationToken = default)
        {
            if (_nodes.ContainsKey(request.Path.Value))
            {
                return ValueTask.FromResult(Rejected(FileSystemOperation.Create, FileSystemErrorCode.AlreadyExists, request.Path));
            }

            string parent = Parent(request.Path.Value);
            if (!_nodes.TryGetValue(parent, out Node? parentNode))
            {
                return ValueTask.FromResult(Rejected(FileSystemOperation.Create, FileSystemErrorCode.NotFound, request.Path));
            }

            if (parentNode.Revision != request.ExpectedParentRevision)
            {
                return ValueTask.FromResult(Rejected(FileSystemOperation.Create, FileSystemErrorCode.RevisionConflict, request.Path));
            }

            Node created = request.Kind == FileSystemEntryKind.Directory ? Node.Directory(1) : Node.File([], 1);
            _nodes[request.Path.Value] = created;
            _nodes[parent] = parentNode with { Revision = parentNode.Revision + 1 };
            FileSystemEntrySnapshot snapshot = Snapshot(request.Path, created);
            return ValueTask.FromResult(new FileSystemMutationResult(
                FileSystemTransactionResult.Committed(Guid.NewGuid(), [snapshot.Metadata.Id]), snapshot));
        }

        public ValueTask<FileSystemMutationResult> DeleteAsync(
            FileSystemDeleteRequest request, CancellationToken cancellationToken = default)
        {
            if (!_nodes.TryGetValue(request.Path.Value, out Node? node))
            {
                return ValueTask.FromResult(Rejected(FileSystemOperation.Delete, FileSystemErrorCode.NotFound, request.Path));
            }

            string parent = Parent(request.Path.Value);
            Node parentNode = _nodes[parent];
            if (node.Revision != request.ExpectedEntryRevision || parentNode.Revision != request.ExpectedParentRevision)
            {
                return ValueTask.FromResult(Rejected(FileSystemOperation.Delete, FileSystemErrorCode.RevisionConflict, request.Path));
            }

            _nodes.Remove(request.Path.Value);
            _nodes[parent] = parentNode with { Revision = parentNode.Revision + 1 };
            return ValueTask.FromResult(new FileSystemMutationResult(
                FileSystemTransactionResult.Committed(Guid.NewGuid(), [])));
        }

        public ValueTask<FileSystemResult<FileSystemEntrySnapshot>> StatAsync(
            FileSystemStatRequest request, CancellationToken cancellationToken = default) =>
            _nodes.TryGetValue(request.Path.Value, out Node? node)
                ? ValueTask.FromResult(FileSystemResult<FileSystemEntrySnapshot>.Success(Snapshot(request.Path, node)))
                : ValueTask.FromResult(FileSystemResult<FileSystemEntrySnapshot>.Failure(
                    Error(FileSystemOperation.Stat, FileSystemErrorCode.NotFound, request.Path)));

        public ValueTask<FileSystemResult<FileSystemDirectorySnapshot>> EnumerateAsync(FileSystemEnumerateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileSystemMutationResult> MoveAsync(FileSystemMoveRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileSystemMutationResult> CopyAsync(FileSystemCopyRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileSystemMutationResult> SetPermissionsAsync(FileSystemSetPermissionsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAppFileSystemGateway WithSelectedHandle(FileSystemSelectedResourceHandle handle) => this;

        private FileSystemEntrySnapshot Snapshot(VirtualPath path, Node node) => new(
            path,
            node.IsDirectory
                ? new DirectoryMetadata(FileSystemEntryId.FromGuid(Guid.NewGuid()), "user", "users", FileSystemPermissions.FromMode(0b111_000_000), new FileSystemTimestamps(_now, _now, _now), node.Revision)
                : new FileMetadata(FileSystemEntryId.FromGuid(Guid.NewGuid()), "user", "users", FileSystemPermissions.FromMode(0b110_000_000), new FileSystemTimestamps(_now, _now, _now), node.Revision, node.Content!.LongLength, "application/json"));

        private static string Parent(string path)
        {
            int separator = path.LastIndexOf('/');
            return separator <= 0 ? "/" : path[..separator];
        }

        private static FileSystemError Error(FileSystemOperation operation, FileSystemErrorCode code, VirtualPath path) => new(operation, code, path);
        private static FileSystemMutationResult Rejected(FileSystemOperation operation, FileSystemErrorCode code, VirtualPath path) => new(FileSystemTransactionResult.Rejected(Guid.NewGuid(), Error(operation, code, path)));

        private sealed record Node(bool IsDirectory, byte[]? Content, long Revision)
        {
            public static Node Directory(long revision) => new(true, null, revision);
            public static Node File(byte[] content, long revision) => new(false, content, revision);
        }
    }
}
