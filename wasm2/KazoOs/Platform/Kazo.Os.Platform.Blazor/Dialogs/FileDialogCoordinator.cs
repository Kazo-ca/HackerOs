using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Simulation.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Blazor.Dialogs;

/// <summary>Describes a queued file dialog that the session renderer may present.</summary>
public abstract record FileDialogPresentation(
    Guid Id,
    string AppId,
    Guid AppInstanceId,
    ProcessId ProcessId,
    string UserId,
    IAppFileSystemGateway FileSystem)
{
    /// <summary>
    /// Gets the requesting app's context: the dialog shows the OS file control with that app's rights
    /// (ADR 0157). <see langword="null"/> (a presentation built by hand) shows the platform's browser
    /// over <see cref="FileSystem"/>.
    /// </summary>
    public IAppExecutionContext? Context { get; init; }
}

/// <summary>Describes an active file-open request.</summary>
public sealed record OpenFileDialogPresentation(
    Guid Id,
    string AppId,
    Guid AppInstanceId,
    ProcessId ProcessId,
    string UserId,
    IAppFileSystemGateway FileSystem,
    OpenFileDialogRequest Request)
    : FileDialogPresentation(Id, AppId, AppInstanceId, ProcessId, UserId, FileSystem);

/// <summary>Describes an active file-save request.</summary>
public sealed record SaveFileDialogPresentation(
    Guid Id,
    string AppId,
    Guid AppInstanceId,
    ProcessId ProcessId,
    string UserId,
    IAppFileSystemGateway FileSystem,
    SaveFileDialogRequest Request)
    : FileDialogPresentation(Id, AppId, AppInstanceId, ProcessId, UserId, FileSystem);

/// <summary>Describes an active folder-selection request.</summary>
public sealed record SelectFolderDialogPresentation(
    Guid Id,
    string AppId,
    Guid AppInstanceId,
    ProcessId ProcessId,
    string UserId,
    IAppFileSystemGateway FileSystem,
    SelectFolderDialogRequest Request)
    : FileDialogPresentation(Id, AppId, AppInstanceId, ProcessId, UserId, FileSystem);

/// <summary>Coordinates one FIFO file-dialog queue for one authenticated session.</summary>
public sealed class FileDialogCoordinator : IFileDialogService, IDisposable
{
    private static readonly TimeSpan DefaultHandleLifetime = TimeSpan.FromMinutes(15);
    private readonly Lock _gate = new();
    private readonly LinkedList<PendingDialog> _pending = [];
    private readonly IFileSystemSelectedResourceHandleRegistry _handleRegistry;
    private readonly TimeSpan _handleLifetime;
    private bool _disposed;

    /// <summary>Creates a coordinator bound to exactly one user session.</summary>
    public FileDialogCoordinator(
        SessionId sessionId,
        IFileSystemSelectedResourceHandleRegistry handleRegistry,
        TimeSpan? handleLifetime = null)
    {
        SessionId = sessionId;
        _handleRegistry = handleRegistry ?? throw new ArgumentNullException(nameof(handleRegistry));
        _handleLifetime = handleLifetime ?? DefaultHandleLifetime;
        if (_handleLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(handleLifetime), "Handle lifetime must be positive.");
        }
    }

    /// <summary>Raised whenever the active presentation changes.</summary>
    public event Action? Changed;

    /// <summary>Gets the session that owns this queue.</summary>
    public SessionId SessionId { get; }

    /// <summary>Gets the request currently eligible for rendering.</summary>
    public FileDialogPresentation? ActiveRequest
    {
        get
        {
            lock (_gate)
            {
                return _pending.First?.Value.Presentation;
            }
        }
    }

    /// <summary>Gets the oldest still-pending request for one owning app instance, independent of other owners.</summary>
    public FileDialogPresentation? ActiveRequestFor(Guid appInstanceId)
    {
        lock (_gate)
        {
            for (LinkedListNode<PendingDialog>? node = _pending.First; node is not null; node = node.Next)
            {
                if (node.Value.Presentation.AppInstanceId == appInstanceId)
                {
                    return node.Value.Presentation;
                }
            }

            return null;
        }
    }

    /// <inheritdoc />
    public ValueTask<OpenFileDialogResult> OpenFileAsync(
        IAppExecutionContext context,
        OpenFileDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(context, AppCapabilities.DialogFileOpen);
        Guid id = Guid.NewGuid();
        return Enqueue<OpenFileDialogResult>(
            new OpenFileDialogPresentation(
                id, context.Manifest.Id, context.InstanceId, context.ProcessId, context.UserId, context.FileSystem, request) { Context = context },
            static () => new OpenFileDialogResult(FileDialogStatus.Cancelled, []),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<SaveFileDialogResult> SaveFileAsync(
        IAppExecutionContext context,
        SaveFileDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(context, AppCapabilities.DialogFileSave);
        Guid id = Guid.NewGuid();
        return Enqueue<SaveFileDialogResult>(
            new SaveFileDialogPresentation(
                id, context.Manifest.Id, context.InstanceId, context.ProcessId, context.UserId, context.FileSystem, request) { Context = context },
            static () => new SaveFileDialogResult(FileDialogStatus.Cancelled, null),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<SelectFolderDialogResult> SelectFolderAsync(
        IAppExecutionContext context,
        SelectFolderDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(context, AppCapabilities.DialogFolderSelect);
        Guid id = Guid.NewGuid();
        return Enqueue<SelectFolderDialogResult>(
            new SelectFolderDialogPresentation(
                id, context.Manifest.Id, context.InstanceId, context.ProcessId, context.UserId, context.FileSystem, request) { Context = context },
            static () => new SelectFolderDialogResult(FileDialogStatus.Cancelled, null),
            cancellationToken);
    }

    /// <summary>Completes the active file-open request with selected paths.</summary>
    public bool SelectOpen(Guid requestId, IReadOnlyList<VirtualPath> paths)
    {
        if (FindActive(requestId) is not OpenFileDialogPresentation presentation)
        {
            return false;
        }

        FileSystemHandleAccess access = FileSystemHandleAccess.Read | FileSystemHandleAccess.Metadata;
        if (presentation.Request.RequestedAccess == SelectedFileAccess.ReadWrite)
        {
            access |= FileSystemHandleAccess.Write;
        }

        SelectedFileResource[] resources = paths.Select(path => new SelectedFileResource(
            path,
            Issue(presentation, path, access))).ToArray();
        return Complete(requestId, new OpenFileDialogResult(FileDialogStatus.Selected, resources));
    }

    /// <summary>Completes the active file-save request with its destination.</summary>
    public bool SelectSave(Guid requestId, VirtualPath path)
    {
        if (FindActive(requestId) is not SaveFileDialogPresentation presentation)
        {
            return false;
        }

        SelectedFileResource resource = new(
            path,
            Issue(
                presentation,
                path,
                FileSystemHandleAccess.Read | FileSystemHandleAccess.Write | FileSystemHandleAccess.Metadata));
        return Complete(requestId, new SaveFileDialogResult(FileDialogStatus.Selected, resource));
    }

    /// <summary>Completes the active folder request with its selected directory.</summary>
    public bool SelectFolder(Guid requestId, VirtualPath path)
    {
        if (FindActive(requestId) is not SelectFolderDialogPresentation presentation)
        {
            return false;
        }

        SelectedFolderResource resource = new(
            path,
            Issue(presentation, path, FileSystemHandleAccess.Enumerate | FileSystemHandleAccess.Metadata));
        return Complete(requestId, new SelectFolderDialogResult(FileDialogStatus.Selected, resource));
    }

    /// <summary>Cancels an active or queued request as an ordinary user outcome.</summary>
    public bool Cancel(Guid requestId) => Complete(requestId, null);

    /// <inheritdoc />
    public void Dispose()
    {
        List<PendingDialog> cancelled;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancelled = [.. _pending];
            _pending.Clear();
        }

        foreach (PendingDialog pending in cancelled)
        {
            pending.Cancel();
            pending.Dispose();
        }

        Changed?.Invoke();
    }

    private ValueTask<TResult> Enqueue<TResult>(
        FileDialogPresentation presentation,
        Func<TResult> cancelledResult,
        CancellationToken cancellationToken)
    {
        PendingDialog<TResult> pending = new(presentation, cancelledResult);
        bool activeChanged;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            activeChanged = !_pending.Any(queued =>
                queued.Presentation.ProcessId == presentation.ProcessId
                && queued.Presentation.AppInstanceId == presentation.AppInstanceId);
            pending.Node = _pending.AddLast(pending);
            pending.CancellationRegistration = cancellationToken.Register(
                static state => ((CancellationState)state!).Coordinator.Cancel(((CancellationState)state).RequestId),
                new CancellationState(this, presentation.Id));
        }

        if (activeChanged)
        {
            Changed?.Invoke();
        }

        return new ValueTask<TResult>(pending.Task);
    }

    private bool Complete(Guid requestId, object? selectedResult)
    {
        PendingDialog? pending;
        bool activeChanged;
        lock (_gate)
        {
            LinkedListNode<PendingDialog>? node = _pending.First;
            while (node is not null && node.Value.Presentation.Id != requestId)
            {
                node = node.Next;
            }

            if (node is null)
            {
                return false;
            }

            pending = node.Value;
            activeChanged = IsOwnerHead(node);
            _pending.Remove(node);
        }

        bool completed = selectedResult is null
            ? pending.Cancel()
            : pending.TryComplete(selectedResult);
        pending.Dispose();
        if (activeChanged)
        {
            Changed?.Invoke();
        }

        return completed;
    }

    private FileDialogPresentation? FindActive(Guid requestId)
    {
        lock (_gate)
        {
            FileDialogPresentation? active = _pending.First?.Value.Presentation;
            return active?.Id == requestId ? active : null;
        }
    }

    /// <summary>Gets whether no earlier queued item shares this node's owning app instance.</summary>
    private static bool IsOwnerHead(LinkedListNode<PendingDialog> node)
    {
        for (LinkedListNode<PendingDialog>? scan = node.List!.First; scan != node; scan = scan!.Next)
        {
            if (scan!.Value.Presentation.ProcessId == node.Value.Presentation.ProcessId
                && scan.Value.Presentation.AppInstanceId == node.Value.Presentation.AppInstanceId)
            {
                return false;
            }
        }

        return true;
    }

    private FileSystemSelectedResourceHandle Issue(
        FileDialogPresentation presentation,
        VirtualPath path,
        FileSystemHandleAccess access) =>
        _handleRegistry.Issue(
            presentation.AppId,
            presentation.UserId,
            path,
            access,
            _handleLifetime,
            presentation.ProcessId);

    private void Validate(IAppExecutionContext context, string capability)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.SessionId != SessionId)
        {
            throw new InvalidOperationException("The app context belongs to a different user session.");
        }

        context.Capabilities.Require(capability);
    }

    private abstract class PendingDialog(FileDialogPresentation presentation) : IDisposable
    {
        public FileDialogPresentation Presentation { get; } = presentation;
        public LinkedListNode<PendingDialog>? Node { get; set; }
        public CancellationTokenRegistration CancellationRegistration { get; set; }
        public abstract bool Cancel();
        public abstract bool TryComplete(object result);
        public void Dispose() => CancellationRegistration.Dispose();
    }

    private sealed class PendingDialog<TResult>(
        FileDialogPresentation presentation,
        Func<TResult> cancelledResult) : PendingDialog(presentation)
    {
        private readonly TaskCompletionSource<TResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TResult> Task => _completion.Task;
        public override bool Cancel() => _completion.TrySetResult(cancelledResult());
        public override bool TryComplete(object result) =>
            result is TResult typed && _completion.TrySetResult(typed);
    }

    private sealed record CancellationState(FileDialogCoordinator Coordinator, Guid RequestId);
}