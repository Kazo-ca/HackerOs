using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Blazor;
using HackerOs.AppSdk.Blazor.Files;
using HackerOs.Platform.Core.Sounds;
using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Simulation.Abstractions.Sounds;

namespace HackerOs.Platform.Blazor.Dialogs;

/// <summary>Describes a queued basic dialog presentation.</summary>
public abstract record DialogPresentation(
    Guid Id,
    string AppId,
    Guid AppInstanceId,
    ProcessId ProcessId,
    string UserId);

/// <summary>Describes an active message-box presentation.</summary>
public sealed record MessageBoxPresentation(
    Guid Id,
    string AppId,
    Guid AppInstanceId,
    ProcessId ProcessId,
    string UserId,
    MessageBoxDialogRequest Request)
    : DialogPresentation(Id, AppId, AppInstanceId, ProcessId, UserId);

/// <summary>Describes an active text-input presentation.</summary>
public sealed record TextInputPresentation(
    Guid Id,
    string AppId,
    Guid AppInstanceId,
    ProcessId ProcessId,
    string UserId,
    TextInputDialogRequest Request)
    : DialogPresentation(Id, AppId, AppInstanceId, ProcessId, UserId);

/// <summary>Describes an active credential-prompt presentation (`ADR 0042`).</summary>
public sealed record CredentialPromptPresentation(
    Guid Id,
    string AppId,
    Guid AppInstanceId,
    ProcessId ProcessId,
    string UserId,
    CredentialPromptDialogRequest Request)
    : DialogPresentation(Id, AppId, AppInstanceId, ProcessId, UserId);

/// <summary>Describes an active app-chooser presentation (ADR 0096, section 5).</summary>
public sealed record AppChooserPresentation(
    Guid Id,
    string AppId,
    Guid AppInstanceId,
    ProcessId ProcessId,
    string UserId,
    AppChooserDialogRequest Request)
    : DialogPresentation(Id, AppId, AppInstanceId, ProcessId, UserId);

/// <summary>
/// Describes an active dialog of the file dialogs contract (<c>hackeros.file-dialogs</c>, ADR 0155 §5):
/// the OS shows the provider's component, modal to the requesting window and with its context.
/// </summary>
/// <param name="Id">The request id.</param>
/// <param name="AppId">The requesting app.</param>
/// <param name="AppInstanceId">The requesting window's instance.</param>
/// <param name="ProcessId">The requesting process.</param>
/// <param name="UserId">The user the dialog is shown to.</param>
/// <param name="Dialog">The dialog's name (<c>HackFileDialogs.OpenWith</c>…).</param>
/// <param name="Request">The request record of that dialog.</param>
/// <param name="Context">The requesting app, whose rights the provider uses (ADR 0148 §4).</param>
public sealed record ProvidedFileDialogPresentation(
    Guid Id,
    string AppId,
    Guid AppInstanceId,
    ProcessId ProcessId,
    string UserId,
    string Dialog,
    object Request,
    IAppExecutionContext Context)
    : DialogPresentation(Id, AppId, AppInstanceId, ProcessId, UserId);

/// <summary>
/// Coordinates basic modal dialogs (MessageBox, TextInput) for applications,
/// delegating file-system dialogs to <see cref="IFileDialogService"/>.
/// </summary>
public sealed class DialogCoordinator : IDialogService, IDisposable
{
    private readonly Lock _gate = new();
    private readonly LinkedList<PendingDialog> _pending = [];
    private readonly IFileDialogService _fileDialogs;
    private readonly OsSoundService? _sounds;
    private bool _disposed;

    /// <summary>Creates a basic dialog coordinator over an injected file dialog service.</summary>
    public DialogCoordinator(IFileDialogService fileDialogs)
        : this(fileDialogs, sounds: null)
    {
    }

    /// <summary>
    /// Creates a basic dialog coordinator that also plays the warning and error sounds of message
    /// boxes (ADR 0204 section 2).
    /// </summary>
    /// <param name="fileDialogs">File dialog service.</param>
    /// <param name="sounds">The session's sound service, or <see langword="null"/> for silent dialogs.</param>
    public DialogCoordinator(IFileDialogService fileDialogs, OsSoundService? sounds)
    {
        _fileDialogs = fileDialogs ?? throw new ArgumentNullException(nameof(fileDialogs));
        _sounds = sounds;
    }

    /// <summary>Raised whenever the active basic presentation changes.</summary>
    public event Action? Changed;

    /// <summary>Gets the basic dialog request currently eligible for rendering.</summary>
    public DialogPresentation? ActiveRequest
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
    public DialogPresentation? ActiveRequestFor(Guid appInstanceId)
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
        CancellationToken cancellationToken = default) =>
        _fileDialogs.OpenFileAsync(context, request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<SaveFileDialogResult> SaveFileAsync(
        IAppExecutionContext context,
        SaveFileDialogRequest request,
        CancellationToken cancellationToken = default) =>
        _fileDialogs.SaveFileAsync(context, request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<SelectFolderDialogResult> SelectFolderAsync(
        IAppExecutionContext context,
        SelectFolderDialogRequest request,
        CancellationToken cancellationToken = default) =>
        _fileDialogs.SelectFolderAsync(context, request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<MessageBoxDialogResult> MessageBoxAsync(
        IAppExecutionContext context,
        MessageBoxDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        // Like Windows' MessageBeep: the icon of the box chooses the sound, played as it is queued.
        if (request.Icon is MessageBoxIcon.Error or MessageBoxIcon.Warning)
        {
            _sounds?.Play(request.Icon == MessageBoxIcon.Error ? OsSoundEvent.Error : OsSoundEvent.Warning);
        }

        Guid id = Guid.NewGuid();
        return Enqueue<MessageBoxDialogResult>(
            new MessageBoxPresentation(
                id, context.Manifest.Id, context.InstanceId, context.ProcessId, context.UserId, request),
            static () => new MessageBoxDialogResult(MessageBoxResult.Cancel),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<TextInputDialogResult> TextInputAsync(
        IAppExecutionContext context,
        TextInputDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        Guid id = Guid.NewGuid();
        return Enqueue<TextInputDialogResult>(
            new TextInputPresentation(
                id, context.Manifest.Id, context.InstanceId, context.ProcessId, context.UserId, request),
            static () => new TextInputDialogResult(TextInputStatus.Cancelled, null),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<CredentialPromptDialogResult> CredentialPromptAsync(
        IAppExecutionContext context,
        CredentialPromptDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        Guid id = Guid.NewGuid();
        return Enqueue<CredentialPromptDialogResult>(
            new CredentialPromptPresentation(
                id, context.Manifest.Id, context.InstanceId, context.ProcessId, context.UserId, request),
            static () => new CredentialPromptDialogResult(CredentialPromptStatus.Cancelled, null, null),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<AppChooserDialogResult> ChooseAppAsync(
        IAppExecutionContext context,
        AppChooserDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        Guid id = Guid.NewGuid();
        return Enqueue<AppChooserDialogResult>(
            new AppChooserPresentation(
                id, context.Manifest.Id, context.InstanceId, context.ProcessId, context.UserId, request),
            static () => new AppChooserDialogResult(AppChooserStatus.Cancelled, null, false),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<OpenWithDialogResult> OpenWithAsync(
        IAppExecutionContext context,
        OpenWithDialogRequest request,
        CancellationToken cancellationToken = default) =>
        EnqueueProvided(
            context,
            HackFileDialogs.OpenWith,
            request,
            static () => new OpenWithDialogResult(OpenWithDialogStatus.Cancelled, null, false),
            cancellationToken);

    /// <inheritdoc />
    public ValueTask<FilePropertiesDialogResult> ShowFilePropertiesAsync(
        IAppExecutionContext context,
        FilePropertiesDialogRequest request,
        CancellationToken cancellationToken = default) =>
        EnqueueProvided(
            context,
            HackFileDialogs.Properties,
            request,
            static () => new FilePropertiesDialogResult(FilePropertiesDialogStatus.Closed),
            cancellationToken);

    /// <inheritdoc />
    public ValueTask<FileConflictDialogResult> ResolveFileConflictAsync(
        IAppExecutionContext context,
        FileConflictDialogRequest request,
        CancellationToken cancellationToken = default) =>
        EnqueueProvided(
            context,
            HackFileDialogs.Conflict,
            request,
            static () => new FileConflictDialogResult(FileConflictDialogChoice.Skip, false),
            cancellationToken);

    /// <summary>
    /// Completes a provided file dialog (ADR 0155 §5) with the result its provider handed back;
    /// <see langword="null"/>, or a result of the wrong type, closes it as cancelled.
    /// </summary>
    /// <param name="requestId">The dialog.</param>
    /// <param name="result">The dialog's result record.</param>
    /// <returns>Whether the dialog was still pending.</returns>
    public bool CompleteProvided(Guid requestId, object? result)
    {
        lock (_gate)
        {
            if (!_pending.Any(pending => pending.Presentation.Id == requestId && pending.Presentation is ProvidedFileDialogPresentation))
            {
                return false;
            }
        }

        return Complete(requestId, result);
    }

    /// <summary>Completes the active app-chooser request with the picked app; refuses an app that was not offered.</summary>
    public bool SelectApp(Guid requestId, string appId, bool alwaysUse)
    {
        if (FindActive(requestId) is not AppChooserPresentation chooser
            || !chooser.Request.Candidates.Any(option => string.Equals(option.AppId, appId, StringComparison.Ordinal)))
        {
            return false;
        }

        return Complete(requestId, new AppChooserDialogResult(AppChooserStatus.Selected, appId, alwaysUse));
    }

    /// <summary>Completes the active credential-prompt request with the submitted login/password.</summary>
    public bool SelectCredentialPrompt(Guid requestId, string loginName, string password)
    {
        if (FindActive(requestId) is not CredentialPromptPresentation)
        {
            return false;
        }

        return Complete(requestId, new CredentialPromptDialogResult(CredentialPromptStatus.Submitted, loginName, password));
    }

    /// <summary>Completes the active message-box request with the clicked button result.</summary>
    public bool SelectMessageBox(Guid requestId, MessageBoxResult result)
    {
        if (FindActive(requestId) is not MessageBoxPresentation)
        {
            return false;
        }

        return Complete(requestId, new MessageBoxDialogResult(result));
    }

    /// <summary>Completes the active text-input request with the submitted value.</summary>
    public bool SelectTextInput(Guid requestId, string? value)
    {
        if (FindActive(requestId) is not TextInputPresentation)
        {
            return false;
        }

        return Complete(requestId, new TextInputDialogResult(TextInputStatus.Submitted, value));
    }

    /// <summary>Cancels an active or queued basic dialog request.</summary>
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
        DialogPresentation presentation,
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

    private ValueTask<TResult> EnqueueProvided<TResult>(
        IAppExecutionContext context,
        string dialog,
        object request,
        Func<TResult> cancelledResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        return Enqueue(
            new ProvidedFileDialogPresentation(
                Guid.NewGuid(), context.Manifest.Id, context.InstanceId, context.ProcessId, context.UserId, dialog, request, context),
            cancelledResult,
            cancellationToken);
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
        if (!completed && selectedResult is not null)
        {
            // A result of the wrong type (a provided dialog's provider) cannot complete the typed
            // task: cancel rather than leave the caller waiting.
            pending.Cancel();
        }

        pending.Dispose();
        if (activeChanged)
        {
            Changed?.Invoke();
        }

        return completed;
    }

    private DialogPresentation? FindActive(Guid requestId)
    {
        lock (_gate)
        {
            DialogPresentation? active = _pending.First?.Value.Presentation;
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

    private abstract class PendingDialog(DialogPresentation presentation) : IDisposable
    {
        public DialogPresentation Presentation { get; } = presentation;
        public LinkedListNode<PendingDialog>? Node { get; set; }
        public CancellationTokenRegistration CancellationRegistration { get; set; }
        public abstract bool Cancel();
        public abstract bool TryComplete(object result);
        public void Dispose() => CancellationRegistration.Dispose();
    }

    private sealed class PendingDialog<TResult>(
        DialogPresentation presentation,
        Func<TResult> cancelledResult) : PendingDialog(presentation)
    {
        private readonly TaskCompletionSource<TResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TResult> Task => _completion.Task;
        public override bool Cancel() => _completion.TrySetResult(cancelledResult());
        public override bool TryComplete(object result) =>
            result is TResult typed && _completion.TrySetResult(typed);
    }

    private sealed record CancellationState(DialogCoordinator Coordinator, Guid RequestId);
}
