using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Platform.Blazor.Dialogs;
using Kazo.Os.Platform.Blazor.Windows;
using Kazo.Os.Simulation.Abstractions;
using Kazo.Os.Windowing.Core;
using Kazo.Os.Windowing.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Blazor.Tests;

public sealed class FileDialogCoordinatorTests
{
    private static readonly SessionId TestSessionId =
        SessionId.FromGuid(Guid.Parse("40000000-0000-0000-0000-000000000001"));

    [Fact]
    public async Task Requests_are_presented_fifo_and_queued_cancellation_is_ordinary()
    {
        RecordingHandleRegistry handles = new();
        using FileDialogCoordinator coordinator = new(TestSessionId, handles, TimeSpan.FromMinutes(5));
        TestExecutionContext context = new(TestSessionId, allowCapabilities: true);
        using CancellationTokenSource queuedCancellation = new();

        Task<OpenFileDialogResult> open = coordinator.OpenFileAsync(
            context, new OpenFileDialogRequest()).AsTask();
        Task<SaveFileDialogResult> save = coordinator.SaveFileAsync(
            context, new SaveFileDialogRequest(), queuedCancellation.Token).AsTask();
        Task<SelectFolderDialogResult> folder = coordinator.SelectFolderAsync(
            context, new SelectFolderDialogRequest()).AsTask();

        OpenFileDialogPresentation activeOpen = Assert.IsType<OpenFileDialogPresentation>(coordinator.ActiveRequest);
        Assert.Same(context.FileSystem, activeOpen.FileSystem);
        // ADR 0157: the dialog shows the OS file control with the requesting app's rights.
        Assert.Same(context, activeOpen.Context);
        queuedCancellation.Cancel();
        Assert.Same(activeOpen, coordinator.ActiveRequest);
        Assert.Equal(FileDialogStatus.Cancelled, (await save).Status);

        VirtualPath selectedFile = VirtualPath.Parse("/home/user/notes.txt");
        Assert.True(coordinator.SelectOpen(activeOpen.Id, [selectedFile]));
        OpenFileDialogResult openResult = await open;
        SelectedFileResource selectedResource = Assert.Single(openResult.Resources);
        Assert.Equal(selectedFile, selectedResource.Path);
        Assert.Equal(FileSystemHandleAccess.Read | FileSystemHandleAccess.Metadata, selectedResource.Handle.Access);

        SelectFolderDialogPresentation activeFolder =
            Assert.IsType<SelectFolderDialogPresentation>(coordinator.ActiveRequest);
        VirtualPath selectedFolder = VirtualPath.Parse("/home/user");
        Assert.True(coordinator.SelectFolder(activeFolder.Id, selectedFolder));
        Assert.Equal(selectedFolder, (await folder).Resource?.Path);
        Assert.Equal(2, handles.Issued.Count);
        Assert.All(handles.Issued, issued =>
        {
            Assert.Equal(context.Manifest.Id, issued.AppId);
            Assert.Equal(context.UserId, issued.UserId);
            Assert.Equal(context.ProcessId, issued.ProcessId);
            Assert.Equal(TimeSpan.FromMinutes(5), issued.ValidFor);
        });
        Assert.Null(coordinator.ActiveRequest);
    }

    [Fact]
    public void Capability_is_required_before_a_request_is_queued()
    {
        using FileDialogCoordinator coordinator = new(TestSessionId, new RecordingHandleRegistry());
        TestExecutionContext context = new(TestSessionId, allowCapabilities: false);

        AppGatewayAccessDeniedException exception = Assert.Throws<AppGatewayAccessDeniedException>(
            () => coordinator.OpenFileAsync(context, new OpenFileDialogRequest()));

        Assert.Equal(AppCapabilities.DialogFileOpen, exception.Capability);
        Assert.Null(coordinator.ActiveRequest);
    }

    [Fact]
    public void Context_from_another_session_is_rejected()
    {
        using FileDialogCoordinator coordinator = new(TestSessionId, new RecordingHandleRegistry());
        TestExecutionContext context = new(SessionId.FromGuid(Guid.NewGuid()), allowCapabilities: true);

        Assert.Throws<InvalidOperationException>(
            () => coordinator.SelectFolderAsync(context, new SelectFolderDialogRequest()));
        Assert.Null(coordinator.ActiveRequest);
    }

    [Fact]
    public async Task Active_request_is_projected_owner_modal_and_completion_returns_focus()
    {
        RecordingHandleRegistry handles = new();
        using FileDialogCoordinator coordinator = new(TestSessionId, handles);
        TestExecutionContext context = new(TestSessionId, allowCapabilities: true);
        WindowRuntime windows = new(new WindowBounds(0, 0, 1200, 800));
        WindowId ownerId = WindowId.FromGuid(Guid.Parse("50000000-0000-0000-0000-000000000001"));
        windows.Apply(new CreateWindowCommand(new WindowRuntimeState(
            ownerId,
            context.Manifest.Id,
            WindowOwnerId.FromGuid(context.InstanceId),
            "Owner",
            null,
            new WindowBounds(100, 80, 800, 600),
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240))));
        Task<OpenFileDialogResult> pending = coordinator.OpenFileAsync(
            context, new OpenFileDialogRequest()).AsTask();

        using FileDialogWindowAdapter adapter = new(coordinator, windows);

        WindowRuntimeState modal = Assert.Single(windows.Windows, window => window.Modality == WindowModality.OwnerModal);
        Assert.Equal(ownerId, modal.OwnerId);
        Assert.True(windows.IsInteractionBlocked(ownerId));
        Assert.True(modal.IsFocused);

        VirtualPath path = VirtualPath.Parse("/home/user/file.txt");
        Assert.True(coordinator.SelectOpen(coordinator.ActiveRequest!.Id, [path]));
        Assert.Equal(path, Assert.Single((await pending).Resources).Path);
        Assert.DoesNotContain(windows.Windows, window => window.Id == modal.Id);
        Assert.True(Assert.Single(windows.Windows, window => window.Id == ownerId).IsFocused);
    }

    [Fact]
    public async Task Independent_owners_get_concurrent_dialogs_without_blocking_unrelated_windows()
    {
        RecordingHandleRegistry handles = new();
        using FileDialogCoordinator coordinator = new(TestSessionId, handles);
        TestExecutionContext contextA = new(TestSessionId, allowCapabilities: true);
        TestExecutionContext contextB = new(TestSessionId, allowCapabilities: true);
        TestExecutionContext contextC = new(TestSessionId, allowCapabilities: true);
        WindowRuntime windows = new(new WindowBounds(0, 0, 1200, 800));

        WindowId ownerAId = WindowId.FromGuid(Guid.Parse("51000000-0000-0000-0000-000000000001"));
        WindowId ownerBId = WindowId.FromGuid(Guid.Parse("51000000-0000-0000-0000-000000000002"));
        WindowId ownerCId = WindowId.FromGuid(Guid.Parse("51000000-0000-0000-0000-000000000003"));
        CreateOwnerWindow(windows, ownerAId, contextA, new WindowBounds(0, 0, 400, 300));
        CreateOwnerWindow(windows, ownerBId, contextB, new WindowBounds(500, 0, 400, 300));
        CreateOwnerWindow(windows, ownerCId, contextC, new WindowBounds(0, 400, 400, 300));

        Task<OpenFileDialogResult> pendingA = coordinator.OpenFileAsync(contextA, new OpenFileDialogRequest()).AsTask();
        Task<SelectFolderDialogResult> pendingB = coordinator.SelectFolderAsync(contextB, new SelectFolderDialogRequest()).AsTask();

        using FileDialogWindowAdapter adapter = new(coordinator, windows);

        WindowRuntimeState modalA = Assert.Single(windows.Windows, w => w.OwnerId == ownerAId);
        WindowRuntimeState modalB = Assert.Single(windows.Windows, w => w.OwnerId == ownerBId);
        Assert.True(windows.IsInteractionBlocked(ownerAId));
        Assert.True(windows.IsInteractionBlocked(ownerBId));
        Assert.False(windows.IsInteractionBlocked(ownerCId));
        Assert.DoesNotContain(windows.Windows, w => w.OwnerId == ownerCId);

        VirtualPath path = VirtualPath.Parse("/home/user/file.txt");
        Assert.True(coordinator.SelectOpen(modalA.Id.Value, [path]));
        Assert.Equal(path, Assert.Single((await pendingA).Resources).Path);
        Assert.DoesNotContain(windows.Windows, w => w.Id == modalA.Id);
        Assert.False(windows.IsInteractionBlocked(ownerAId));
        Assert.True(windows.IsInteractionBlocked(ownerBId));
        Assert.Contains(windows.Windows, w => w.Id == modalB.Id);

        VirtualPath folder = VirtualPath.Parse("/home/user");
        Assert.True(coordinator.SelectFolder(modalB.Id.Value, folder));
        Assert.Equal(folder, (await pendingB).Resource?.Path);
        Assert.DoesNotContain(windows.Windows, w => w.Id == modalB.Id);
        Assert.False(windows.IsInteractionBlocked(ownerBId));
    }

    private static void CreateOwnerWindow(WindowRuntime windows, WindowId ownerId, TestExecutionContext context, WindowBounds bounds) =>
        windows.Apply(new CreateWindowCommand(new WindowRuntimeState(
            ownerId,
            context.Manifest.Id,
            WindowOwnerId.FromGuid(context.InstanceId),
            "Owner",
            null,
            bounds,
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240))));

    private sealed class TestExecutionContext(SessionId sessionId, bool allowCapabilities) : IAppExecutionContext
    {
        public AppManifest Manifest { get; } = new()
        {
            Id = "org.hackeros.dialog-test",
            Name = "Dialog Test",
            Version = "1.0.0",
            PublisherId = "org.hackeros",
            Description = "Dialog coordinator test app.",
            Kind = AppKind.Window,
            EntryPoint = new AppEntryPointManifest("Tests", "DialogTest"),
            SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
            Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
            Resources = AppResourceProfileManifest.None,
        };

        public Guid InstanceId { get; } = Guid.NewGuid();
        public string UserId => "user";
        public AppAuthority UserAuthority => AppAuthority.User;
        public IReadOnlySet<string> GrantedCapabilities { get; } = new HashSet<string>();
        public SessionId SessionId { get; } = sessionId;
        public ProcessId ProcessId { get; } = ProcessId.FromInt64(7);
        public CancellationToken CancellationToken => CancellationToken.None;
        public ICapabilityChecker Capabilities { get; } = new TestCapabilityChecker(allowCapabilities);
        public IAppFileSystemGateway FileSystem { get; } = new StrictFileSystemGateway();
        public IAppEventGateway Events => throw new NotSupportedException();
        public IAppNotificationGateway Notifications => throw new NotSupportedException();
        public IAppLoggingGateway Logging => throw new NotSupportedException();
        public IAppClockGateway Clock => throw new NotSupportedException();
        public IAppProcessGateway Processes => throw new NotSupportedException();
    }

    private sealed class StrictFileSystemGateway : IAppFileSystemGateway
    {
        public ValueTask<FileSystemResult<FileSystemContentReadHandle>> ReadAsync(FileSystemReadRequest request, CancellationToken cancellationToken = default) => throw Unexpected();
        public ValueTask<FileSystemResult<FileSystemDirectorySnapshot>> EnumerateAsync(FileSystemEnumerateRequest request, CancellationToken cancellationToken = default) => throw Unexpected();
        public ValueTask<FileSystemMutationResult> CreateAsync(FileSystemCreateRequest request, CancellationToken cancellationToken = default) => throw Unexpected();
        public ValueTask<FileSystemMutationResult> WriteAsync(FileSystemWriteRequest request, IFileSystemContentSource content, CancellationToken cancellationToken = default) => throw Unexpected();
        public ValueTask<FileSystemMutationResult> MoveAsync(FileSystemMoveRequest request, CancellationToken cancellationToken = default) => throw Unexpected();
        public ValueTask<FileSystemMutationResult> CopyAsync(FileSystemCopyRequest request, CancellationToken cancellationToken = default) => throw Unexpected();
        public ValueTask<FileSystemMutationResult> DeleteAsync(FileSystemDeleteRequest request, CancellationToken cancellationToken = default) => throw Unexpected();
        public ValueTask<FileSystemResult<FileSystemEntrySnapshot>> StatAsync(FileSystemStatRequest request, CancellationToken cancellationToken = default) => throw Unexpected();
        public ValueTask<FileSystemMutationResult> SetPermissionsAsync(FileSystemSetPermissionsRequest request, CancellationToken cancellationToken = default) => throw Unexpected();
        public IAppFileSystemGateway WithSelectedHandle(FileSystemSelectedResourceHandle handle) => throw Unexpected();
        private static InvalidOperationException Unexpected() => new("The coordinator must not access filesystem data.");
    }

    private sealed class RecordingHandleRegistry : IFileSystemSelectedResourceHandleRegistry
    {
        public List<IssuedHandle> Issued { get; } = [];

        public FileSystemSelectedResourceHandle Issue(
            string appId,
            string userId,
            VirtualPath path,
            FileSystemHandleAccess access,
            TimeSpan validFor,
            ProcessId? issuedToProcessId = null)
        {
            Issued.Add(new IssuedHandle(appId, userId, path, access, validFor, issuedToProcessId));
            DateTimeOffset issuedAt = new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
            return new FileSystemSelectedResourceHandle(
                Guid.NewGuid(), appId, userId, path, access, issuedAt, issuedAt + validFor, 1);
        }

        public bool TryGet(Guid handleId, out FileSystemSelectedResourceHandle handle) { handle = null!; return false; }
        public bool Revoke(Guid handleId) => false;
        public int RevokeAllForProcess(ProcessId processId) => 0;
        public int RevokeAllForUser(string userId) => 0;
        public int RevokeAllForApp(string appId) => 0;
    }

    private sealed record IssuedHandle(
        string AppId,
        string UserId,
        VirtualPath Path,
        FileSystemHandleAccess Access,
        TimeSpan ValidFor,
        ProcessId? ProcessId);

    private sealed class TestCapabilityChecker(bool allowCapabilities) : ICapabilityChecker
    {
        public CapabilityPolicyEvaluation Evaluate(
            string capability,
            AppAuthority requiredAuthority = AppAuthority.User,
            CapabilityResourceCandidate? resourceCandidate = null) =>
            CapabilityPolicyEvaluation.DenyMissing(1);

        public void Require(
            string capability,
            AppAuthority requiredAuthority = AppAuthority.User,
            CapabilityResourceCandidate? resourceCandidate = null)
        {
            if (!allowCapabilities)
            {
                throw new AppGatewayAccessDeniedException(capability, CapabilityPolicyEvaluation.DenyMissing(1));
            }
        }
    }
}