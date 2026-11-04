using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Blazor.Files;
using Kazo.Os.AppSdk.Clipboard;
using Kazo.Os.Apps.FileExplorer.FileControl;
using Kazo.Os.Apps.FileExplorer.FileControl.Events;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Notifications;

namespace Kazo.Os.Apps.FileExplorer;

/// <summary>
/// The commands of the file control (ADR 0155 §3): what the explorer window did around
/// <c>FileView</c> before the control became an OS contract. Each one runs in the host's ambient context
/// (the menu, the keys and the drops reach it through callbacks of this component), so the trash, the
/// clipboard and the copies are the host's.
/// </summary>
public sealed partial class ExplorerFileView
{
    // ---- Opening -------------------------------------------------------------------------------------

    /// <summary>The menu's or the toolbar's Open: every file of the selection, or the one folder.</summary>
    private async Task OpenItemsAsync(IReadOnlyList<FileViewItem> items)
    {
        if (_view is null)
        {
            return;
        }

        if (items is [{ IsDirectory: true } folder])
        {
            await _view.ActivateItemAsync(folder);
            return;
        }

        foreach (FileViewItem item in items.Where(item => !item.IsDirectory))
        {
            await _view.ActivateItemAsync(item);
        }
    }

    /// <summary>Opens a file with its default app (ADR 0039, 0121), or asks which app when several can.</summary>
    private async Task OpenFileAsync(FileViewItem item)
    {
        if (await RaiseOpeningAsync(item) || await TryOpenArchiveItemAsync(item) || await TryLaunchShortcutAsync(item))
        {
            return;
        }

        await OpenPathAsync(item.FullPath);
    }

    /// <summary>Opens a file by its path with its default app, or asks which app when several can.</summary>
    private async Task OpenPathAsync(VirtualPath path)
    {
        AppIntentOpenFileResult result;
        try
        {
            result = await HostContext.Intents.OpenFileAsync(path, cancellationToken: HostContext.CancellationToken);
        }
        catch (Exception exception) when (exception is AppGatewayAccessDeniedException or NotSupportedException)
        {
            await ReportErrorAsync(L["Error.OpenDenied"]);
            return;
        }

        switch (result.Outcome)
        {
            case AppIntentOpenFileOutcome.Opened:
                break;
            case AppIntentOpenFileOutcome.ChooserRequired:
                await ChooseAppAsync(path, result.CandidateAppIds ?? []);
                break;
            case AppIntentOpenFileOutcome.NoHandler:
                await ReportErrorAsync(L["Error.NoHandler"]);
                break;
            default:
                await ReportErrorAsync(L["Error.OpenFailed", result.ErrorCode ?? string.Empty]);
                break;
        }
    }

    /// <summary>
    /// "Choose another app…" (or the choice the OS asks for when several apps can open the file): the
    /// provided "Open with" dialog, then "Always use" written as the user's default (ADR 0121 §4).
    /// </summary>
    /// <param name="path">The file to open.</param>
    /// <param name="candidateAppIds">The apps to offer; <see langword="null"/> offers every window app.</param>
    private async Task ChooseAppAsync(VirtualPath path, IReadOnlyList<string>? candidateAppIds)
    {
        if (Dialogs is not { } dialogs)
        {
            await ReportErrorAsync(L["Error.DialogsUnavailable"]);
            return;
        }

        OpenWithDialogResult choice = await dialogs.OpenWithAsync(
            HostContext,
            new OpenWithDialogRequest(path.Value) { CandidateAppIds = candidateAppIds },
            HostContext.CancellationToken);
        switch (choice.Status)
        {
            case OpenWithDialogStatus.Selected when choice.AppId is { } appId:
                if (choice.AlwaysUse)
                {
                    await RememberChoiceAsync(appId, path.Value);
                }

                await LaunchWithAsync(appId, path.Value);
                break;
            case OpenWithDialogStatus.Unavailable:
                await ReportErrorAsync(L["Error.DialogsUnavailable"]);
                break;
        }
    }

    /// <summary>Opens the item with exactly one app (an "Open with" entry), bypassing the associations.</summary>
    private async Task LaunchWithAsync(string appId, string fullPath)
    {
        try
        {
            await HostContext.Intents.LaunchAsync(appId, [fullPath], HostContext.CancellationToken);
        }
        catch (Exception exception) when (exception is AppGatewayAccessDeniedException or NotSupportedException)
        {
            await ReportErrorAsync(L["Error.LaunchDenied"]);
        }
    }

    /// <summary>Writes "Always use this app" as the user's default for the file's extension (ADR 0121 §4).</summary>
    private async Task RememberChoiceAsync(string appId, string fullPath)
    {
        if (ExtensionOf(fullPath) is not { } extension)
        {
            return;
        }

        KazoDefaultHandlerStatus status;
        try
        {
            status = await HostContext.Catalog.SetUserDefaultHandlerAsync(extension, "open", appId, HostContext.CancellationToken);
        }
        catch (Exception exception) when (exception is AppGatewayAccessDeniedException or NotSupportedException)
        {
            status = KazoDefaultHandlerStatus.WriteFailed;
        }

        if (status != KazoDefaultHandlerStatus.Changed)
        {
            await ReportErrorAsync(L["Error.RememberFailed", extension]);
        }
    }

    /// <summary>The lowercase extension of a path (<c>.md</c>), or <see langword="null"/> when it has none.</summary>
    internal static string? ExtensionOf(string fullPath)
    {
        string extension = Path.GetExtension(fullPath);
        return extension.Length > 1 ? extension.ToLowerInvariant() : null;
    }

    // ---- Rename and properties -----------------------------------------------------------------------

    private Task RenameAsync(FileViewItem item)
    {
        if (!item.IsSelected)
        {
            item.Select();
        }

        item.Rename();
        return Task.CompletedTask;
    }

    /// <summary>The provided properties dialog, modal to the host's window.</summary>
    private async Task ShowPropertiesAsync(string path)
    {
        if (Dialogs is not { } dialogs)
        {
            await ReportErrorAsync(L["Error.DialogsUnavailable"]);
            return;
        }

        FilePropertiesDialogResult result = await dialogs.ShowFilePropertiesAsync(
            HostContext, new FilePropertiesDialogRequest(path), HostContext.CancellationToken);
        if (result.Status == FilePropertiesDialogStatus.Unavailable)
        {
            await ReportErrorAsync(L["Error.DialogsUnavailable"]);
        }
    }

    // ---- Deletion (ADR 0052, section 5) --------------------------------------------------------------

    /// <summary>Whether one of <paramref name="items"/> is on a network drive (a folder of the computer, ADR 0181).</summary>
    private async Task<bool> AnyOnNetworkDriveAsync(IReadOnlyList<FileViewItem> items)
    {
        KazoDriveInfo[] drives;
        try
        {
            drives = await KazoDriveInfo.GetDrivesAsync(HostContext.CancellationToken);
        }
        catch (NotSupportedException)
        {
            return false;
        }

        return items.Any(item => drives.Any(drive => drive.DriveType == KazoDriveType.Network
            && item.FullPath.Value.StartsWith(drive.Name + "/", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Moves <paramref name="items"/> to the trash, or — for Shift+Delete / "Delete permanently" and only
    /// after a confirmation — deletes them for good, with the host's permissions. A network drive has no
    /// trash (ADR 0181, section 10): its items are always deleted for good, after the confirmation.
    /// </summary>
    private async Task DeleteItemsAsync(IReadOnlyList<FileViewItem> items, bool permanent)
    {
        if (_view is null || items.Count == 0 || !Has(KazoFileViewFeatures.Delete))
        {
            return;
        }

        if (await RefuseArchiveEntriesAsync(items))
        {
            return;
        }

        permanent = permanent || await AnyOnNetworkDriveAsync(items);

        if (permanent)
        {
            if (Dialogs is not { } dialogs)
            {
                await ReportErrorAsync(L["Error.DialogsUnavailable"]);
                return;
            }

            string question = items.Count == 1
                ? L["Dialog.DeletePermanently.One", items[0].FileName]
                : L["Dialog.DeletePermanently.Many", items.Count];
            MessageBoxDialogResult answer = await dialogs.MessageBoxAsync(
                HostContext,
                new MessageBoxDialogRequest { Title = L["Dialog.DeletePermanently.Title"], Content = question, DialogType = MessageBoxType.YesNo },
                HostContext.CancellationToken);
            if (answer.Result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        List<string> failures = [];
        List<string> removed = [];
        // Stays on the window's thread, in the host's ambient context (ADR 0140): each item is an
        // awaited OS call, with a checkpoint between two.
        async Task DeleteAllAsync()
        {
            foreach (FileViewItem item in items)
            {
                try
                {
                    if (!permanent)
                    {
                        await KazoTrash.MoveToTrashAsync(item.FullPath.Value, HostContext.CancellationToken);
                    }
                    else if (item.Metadata.Kind == FileSystemEntryKind.Directory)
                    {
                        await KazoDirectory.DeleteAsync(item.FullPath.Value, recursive: true, HostContext.CancellationToken);
                    }
                    else
                    {
                        await KazoFile.DeleteAsync(item.FullPath.Value, HostContext.CancellationToken);
                    }

                    removed.Add(item.FullPath.Value);
                }
                catch (UnauthorizedAccessException)
                {
                    failures.Add(L["Error.ItemPermissionDenied", item.FileName]);
                }
                catch (IOException exception)
                {
                    failures.Add(L["Error.Item", item.FileName, exception.Message]);
                }
            }
        }

        await TrackOperation(DeleteAllAsync());

        _view.ClearSelect();
        await RefreshAfterChangeAsync();
        if (failures.Count > 0)
        {
            await ReportErrorAsync(L["Error.Delete", string.Join("; ", failures)]);
        }

        await RefreshTrashStateAsync();
        if (removed.Count > 0 && Deleted.HasDelegate)
        {
            await Deleted.InvokeAsync(new KazoFileViewDeletedEventArgs(removed, permanent));
        }
    }

    // ---- Clipboard (ADR 0056 section 12, ADR 0058 section 1) -----------------------------------------

    /// <summary>The view's Ctrl+X/C/V and pasted host files.</summary>
    private Task OnClipboardRequestedAsync(FileViewClipboardRequestedEventArgs e) => e.Action switch
    {
        FileViewClipboardAction.Cut => PutOnClipboardAsync(e.Items, KazoClipboardFileOperation.Cut),
        FileViewClipboardAction.Copy => PutOnClipboardAsync(e.Items, KazoClipboardFileOperation.Copy),
        _ => PasteIntoAsync(e.Destination)
    };

    /// <summary>Cut or copy: writes the paths to the session clipboard. Nothing moves before the paste.</summary>
    private async Task PutOnClipboardAsync(IReadOnlyList<FileViewItem> items, KazoClipboardFileOperation operation)
    {
        if (items.Count == 0 || !Has(KazoFileViewFeatures.Clipboard))
        {
            return;
        }

        // A cut would remove the items from the archive at the paste: an archive is only copied from (ADR 0185 §9).
        if (operation == KazoClipboardFileOperation.Cut && await RefuseArchiveEntriesAsync(items))
        {
            return;
        }

        try
        {
            await KazoClipboard.SetFileDropListAsync(items.Select(item => item.FullPath.Value), operation, HostContext.CancellationToken);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or NotSupportedException)
        {
            await ReportErrorAsync(L["Error.Clipboard"]);
        }
    }

    /// <summary>
    /// Paste: copies (or, for cut files, moves) the clipboard's files into <paramref name="destination"/>.
    /// After a cut, the clipboard is emptied once every file was moved.
    /// </summary>
    private async Task PasteIntoAsync(VirtualPath destination)
    {
        if (!Has(KazoFileViewFeatures.Clipboard) || await RefuseInArchiveAsync(destination))
        {
            return;
        }

        KazoClipboardFileList? list;
        try
        {
            list = await KazoClipboard.GetFileDropListAsync(HostContext.CancellationToken);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or NotSupportedException)
        {
            await ReportErrorAsync(L["Error.Clipboard"]);
            return;
        }

        if (list is null || list.Paths.Count == 0)
        {
            return;
        }

        bool cut = list.Operation == KazoClipboardFileOperation.Cut;
        // Files copied from an archive are extracted (ADR 0185 §9); the others are copied or moved.
        (IReadOnlyList<string> others, KazoFileTransferResult extracted) = await TrackOperation(ExtractFromArchivesAsync(list.Paths, destination));
        // Copies stay on the window's thread (ADR 0140): each item is an awaited OS call with a
        // checkpoint between two, and a name conflict asks the user between two items.
        KazoFileTransferResult result = others.Count == 0 ? extracted : Merge(extracted, await TrackOperation(KazoFileOperations.TransferAsync(
            others,
            destination.Value,
            cut ? KazoFileTransferMode.Move : KazoFileTransferMode.Copy,
            AskConflictAsync,
            HostContext.CancellationToken)));

        if (cut && result.Succeeded)
        {
            await KazoClipboard.ClearAsync(HostContext.CancellationToken);
        }

        await ReportTransferAsync(result, cut ? TransferVerb.Move : TransferVerb.Copy);
        await RefreshAfterChangeAsync();
    }

    private void OnClipboardChanged(object? sender, EventArgs e) =>
        _ = InvokeAsync(async () =>
        {
            await ReadClipboardStateAsync();
            StateHasChanged();
            RaiseStateChanged();
        });

    /// <summary>Reads whether there is something to paste and which items to dim as cut.</summary>
    private async Task ReadClipboardStateAsync()
    {
        try
        {
            KazoClipboardFileList? list = await HostContext.Clipboard.GetFileDropListAsync(HostContext.CancellationToken);
            _canPaste = list is { Paths.Count: > 0 };
            _cutPaths = list is { Operation: KazoClipboardFileOperation.Cut }
                ? new HashSet<string>(list.Paths, StringComparer.Ordinal)
                : [];
        }
        catch (Exception exception) when (exception is AppGatewayAccessDeniedException or NotSupportedException)
        {
            _canPaste = false;
            _cutPaths = [];
        }
    }

    // ---- Drag and drop (ADR 0058 section 1) ----------------------------------------------------------

    /// <summary>Every drop the view hands over, into the folder it was dropped on (<see cref="TransferDropAsync"/>).</summary>
    private Task OnDropRequestedAsync(FileViewDropRequestedEventArgs e) =>
        e.Destination is { } destination ? TransferDropAsync(e, destination) : Task.CompletedTask;

    /// <summary>
    /// A drop into <paramref name="destination"/>: Kazo OS entries are moved (copied with Ctrl), with the
    /// conflict dialog; files from the host system are imported; apps from the Start menu become
    /// <c>.desktop</c> shortcuts (ADR 0160 §6). One summary covers the transfers.
    /// </summary>
    private async Task TransferDropAsync(FileViewDropRequestedEventArgs e, VirtualPath destination)
    {
        if (!Has(KazoFileViewFeatures.DragDrop) && e.Entries.Count > 0)
        {
            return;
        }

        // Nothing goes into an archive (ADR 0185 §9).
        if (await RefuseInArchiveAsync(destination))
        {
            return;
        }

        await CreateShortcutsAsync(e.AppIds, destination);

        List<KazoFileTransferItemResult> completed = [];
        List<string> skipped = [];
        List<KazoFileTransferFailure> failures = [];

        // Items dragged out of an archive are extracted, never moved out of it (ADR 0185 §9).
        (IReadOnlyList<string> others, KazoFileTransferResult extracted) = await TrackOperation(
            ExtractFromArchivesAsync([.. e.Entries.Select(entry => entry.Path.Value)], destination));
        completed.AddRange(extracted.Completed);
        skipped.AddRange(extracted.Skipped);
        failures.AddRange(extracted.Failures);

        if (others.Count > 0)
        {
            KazoFileTransferResult moved = await TrackOperation(KazoFileOperations.TransferAsync(
                others,
                destination.Value,
                e.Copy ? KazoFileTransferMode.Copy : KazoFileTransferMode.Move,
                AskConflictAsync,
                HostContext.CancellationToken));
            completed.AddRange(moved.Completed);
            skipped.AddRange(moved.Skipped);
            failures.AddRange(moved.Failures);
        }

        if (e.HostFiles.Count > 0)
        {
            KazoFileTransferResult imported = await TrackOperation(KazoFileOperations.ImportAsync(e.HostFiles, destination.Value, HostContext.CancellationToken));
            completed.AddRange(imported.Completed);
            failures.AddRange(imported.Failures);
        }

        await ReportTransferAsync(
            new KazoFileTransferResult(completed, skipped, failures),
            e.Copy || e.Entries.Count == 0 ? TransferVerb.Copy : TransferVerb.Move);
        await RefreshAfterChangeAsync();
    }

    /// <summary>The provided "Replace / Keep both / Skip" dialog; closing it skips the item.</summary>
    private async Task<KazoFileConflictResolution> AskConflictAsync(KazoFileTransferConflict conflict)
    {
        if (Dialogs is not { } dialogs)
        {
            return new KazoFileConflictResolution(KazoFileConflictChoice.Skip);
        }

        FileConflictDialogResult answer = await dialogs.ResolveFileConflictAsync(
            HostContext,
            new FileConflictDialogRequest(conflict.SourcePath, conflict.DestinationPath, conflict.SourceIsDirectory, conflict.DestinationIsDirectory),
            HostContext.CancellationToken);
        KazoFileConflictChoice choice = answer.Choice switch
        {
            FileConflictDialogChoice.Replace => KazoFileConflictChoice.Replace,
            FileConflictDialogChoice.KeepBoth => KazoFileConflictChoice.KeepBoth,
            _ => KazoFileConflictChoice.Skip
        };
        return new KazoFileConflictResolution(choice, answer.ApplyToAll);
    }

    // ---- Shared ----------------------------------------------------------------------------------------

    /// <summary>
    /// Records an operation so a closing window can wait for it: each item moves atomically and the
    /// transfer stops between two when the process is asked to stop (ADR 0069).
    /// </summary>
    private async Task<T> TrackOperation<T>(Task<T> operation)
    {
        await TrackOperation((Task)operation);
        return await operation;
    }

    private async Task TrackOperation(Task operation)
    {
        _operations.Add(operation);
        RaiseStateChanged();
        try
        {
            await operation;
        }
        finally
        {
            _operations.Remove(operation);
            RaiseStateChanged();
        }
    }

    /// <summary>The end-of-operation summary: the failures (with their reason) to the host; nothing when all went through.</summary>
    private async Task ReportTransferAsync(KazoFileTransferResult result, TransferVerb verb)
    {
        if (ExplorerTransferReport.DescribeFailures(L, result, verb) is { } summary)
        {
            await ReportErrorAsync(summary);
        }
    }

    private async Task RefreshAfterChangeAsync()
    {
        if (_view is null)
        {
            return;
        }

        try
        {
            await _view.RefreshAsync();
        }
        catch (InvalidOperationException)
        {
            // The folder shown was moved away or deleted: show the home folder instead.
            try
            {
                await _view.NavigateAsync(VirtualPath.Parse(Kazo.Os.AppSdk.KazoUser.Describe(HostContext).HomeDirectory));
            }
            catch (InvalidOperationException exception)
            {
                await ReportErrorAsync(exception.Message);
            }
        }

        RaiseStateChanged();
    }
}
