using System.Text.Json;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Blazor;
using HackerOs.Platform.WorkerHost.Worker;

namespace HackerOs.Platform.RemoteRendering.Worker;

/// <summary>
/// The SDK's dialogs for a window rendered in a worker (ADR 0133, section 3): the request goes to the
/// window's host on the page (<c>{k:"dlg"}</c>), which opens the OS's own dialog for the process and
/// sends the answer back (<c>{k:"dlgr"}</c>). The trial covers the message box and the text input;
/// the file dialogs hand out live OS objects and are refused here; the credential prompt answers "cancelled".
/// </summary>
internal sealed class RemoteDialogService(WorkerWindowRun run) : IDialogService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = [];
    private int _next;

    /// <inheritdoc />
    public async ValueTask<MessageBoxDialogResult> MessageBoxAsync(
        IAppExecutionContext context, MessageBoxDialogRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        JsonElement answer = await AskAsync(writer =>
        {
            writer.WriteString("ty", "msg");
            writer.WriteString("title", request.Title);
            writer.WriteString("content", request.Content);
            writer.WriteNumber("type", (int)request.DialogType);
            writer.WriteNumber("icon", (int)request.Icon);
        }, cancellationToken).ConfigureAwait(true);
        return new MessageBoxDialogResult((MessageBoxResult)answer.GetProperty("r").GetInt32());
    }

    /// <inheritdoc />
    public async ValueTask<TextInputDialogResult> TextInputAsync(
        IAppExecutionContext context, TextInputDialogRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        JsonElement answer = await AskAsync(writer =>
        {
            writer.WriteString("ty", "txt");
            writer.WriteString("title", request.Title);
            writer.WriteString("content", request.Content);
            if (request.DefaultValue is { } value)
            {
                writer.WriteString("default", value);
            }

            if (request.Placeholder is { } placeholder)
            {
                writer.WriteString("placeholder", placeholder);
            }
        }, cancellationToken).ConfigureAwait(true);
        return new TextInputDialogResult(
            (TextInputStatus)answer.GetProperty("r").GetInt32(),
            answer.TryGetProperty("v", out JsonElement text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A password never crosses into a worker in the trial: the prompt answers "cancelled", so a denied
    /// permission ends with the plain "Permission denied" message box (ADR 0133, section 4).
    /// </remarks>
    public ValueTask<CredentialPromptDialogResult> CredentialPromptAsync(
        IAppExecutionContext context, CredentialPromptDialogRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new CredentialPromptDialogResult(CredentialPromptStatus.Cancelled, null, null));

    /// <inheritdoc />
    public ValueTask<OpenFileDialogResult> OpenFileAsync(
        IAppExecutionContext context, OpenFileDialogRequest request, CancellationToken cancellationToken = default) =>
        throw NotInWorker("the file dialogs");

    /// <inheritdoc />
    public ValueTask<SaveFileDialogResult> SaveFileAsync(
        IAppExecutionContext context, SaveFileDialogRequest request, CancellationToken cancellationToken = default) =>
        throw NotInWorker("the file dialogs");

    /// <inheritdoc />
    public ValueTask<SelectFolderDialogResult> SelectFolderAsync(
        IAppExecutionContext context, SelectFolderDialogRequest request, CancellationToken cancellationToken = default) =>
        throw NotInWorker("the file dialogs");

    /// <summary>Hands an answer from the page to its waiting call.</summary>
    public void Complete(JsonElement answer)
    {
        TaskCompletionSource<JsonElement>? pending;
        lock (_gate)
        {
            _pending.Remove(answer.GetProperty("id").GetInt32(), out pending);
        }

        pending?.TrySetResult(answer);
    }

    /// <summary>The window closed: dialogs still open are cancelled.</summary>
    public void CancelAll()
    {
        List<TaskCompletionSource<JsonElement>> pending;
        lock (_gate)
        {
            pending = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (TaskCompletionSource<JsonElement> call in pending)
        {
            call.TrySetCanceled();
        }
    }

    private async Task<JsonElement> AskAsync(Action<Utf8JsonWriter> request, CancellationToken cancellationToken)
    {
        TaskCompletionSource<JsonElement> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int id;
        lock (_gate)
        {
            id = ++_next;
            _pending[id] = answer;
        }

        run.Send(writer =>
        {
            writer.WriteString("k", "dlg");
            writer.WriteNumber("id", id);
            request(writer);
        });
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                _pending.Remove(id);
            }

            answer.TrySetCanceled(cancellationToken);
        });
        return await answer.Task.ConfigureAwait(true);
    }

    private static NotSupportedException NotInWorker(string what) =>
        new($"A window rendered in a worker cannot open {what} yet (ADR 0133).");
}
