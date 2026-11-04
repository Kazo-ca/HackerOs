using Kazo.Os.AppSdk.IO;

namespace Kazo.Os.AppSdk.Blazor.Files;

/// <summary>
/// Connects the copy and move rules of the SDK (<see cref="KazoFileOperations"/>, ADR 0161 §2) to the
/// name conflict dialog of the <c>kazoos.file-dialogs</c> provider.
/// </summary>
public static class KazoFileOperationDialogs
{
    /// <summary>
    /// Gets a resolver that asks the user "Replace / Keep both / Skip" through
    /// <see cref="IDialogService.ResolveFileConflictAsync"/>, modal to <paramref name="owner"/>'s window
    /// and with its rights. Closing the dialog, or a host without the dialog, skips the item.
    /// </summary>
    /// <param name="dialogs">The dialog service of the app.</param>
    /// <param name="owner">The app that runs the operation.</param>
    /// <param name="cancellationToken">Closes a dialog still open when the operation is abandoned.</param>
    /// <returns>A resolver for <see cref="KazoFileOperations.MoveAsync"/> and <see cref="KazoFileOperations.TransferAsync"/>.</returns>
    public static KazoFileConflictResolver AsFileConflictResolver(
        this IDialogService dialogs, IAppExecutionContext owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(owner);
        return async conflict =>
        {
            FileConflictDialogResult answer = await dialogs.ResolveFileConflictAsync(
                owner,
                new FileConflictDialogRequest(conflict.SourcePath, conflict.DestinationPath, conflict.SourceIsDirectory, conflict.DestinationIsDirectory),
                cancellationToken).ConfigureAwait(false);
            KazoFileConflictChoice choice = answer.Choice switch
            {
                FileConflictDialogChoice.Replace => KazoFileConflictChoice.Replace,
                FileConflictDialogChoice.KeepBoth => KazoFileConflictChoice.KeepBoth,
                _ => KazoFileConflictChoice.Skip
            };
            return new KazoFileConflictResolution(choice, answer.ApplyToAll);
        };
    }
}
