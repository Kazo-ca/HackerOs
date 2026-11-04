using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.AppSdk.IO;

/// <summary>
/// Copy, move, import and trash operations behind cut/copy/paste and drag-and-drop (ADR 0058,
/// section 1; in the SDK since 2.0, ADR 0161 §2, so every app and every provider of the file
/// control applies the same rules), shaped on Nautilus: a copy whose name is taken becomes <c>name (copy).ext</c>, then
/// <c>name (copy 2).ext</c>…; a move whose name is taken asks Replace, Keep both or Skip. A folder
/// is never copied or moved into itself; an error on one source never stops the others.
/// </summary>
/// <remarks>
/// Every member runs as the ambient app through the public <c>Hack*</c> API (ADR 0043), so the app's
/// capabilities and the file permissions apply to each step. Call it from an app event handler (a
/// <c>KazoComponentBase</c> handler is in scope) or inside <see cref="KazoExecutionContext.Enter"/>.
/// The failure reasons are written in the language of that ambient app's process (ADR 0104).
/// A window app asks the user about a name conflict with
/// <c>IDialogService.AsFileConflictResolver</c> (<c>Kazo.Os.AppSdk.Blazor</c>), which shows the
/// dialog of the <c>hackeros.file-dialogs</c> provider; a command passes its own resolver or none.
/// </remarks>
public static class KazoFileOperations
{
    /// <summary>Gets the library's texts in the ambient app's language.</summary>
    private static Microsoft.Extensions.Localization.IStringLocalizer<Strings> Text => KazoLocalization.GetLocalizer<Strings>();

    /// <summary>Largest host file an import reads (256 MiB); a bigger one is reported as a failure.</summary>
    public const long MaxHostFileBytes = 256L * 1024 * 1024;

    /// <summary>Copies sources into a directory; a taken name gets a <c>(copy)</c> suffix, without asking.</summary>
    /// <param name="sources">Absolute paths of files and directories (directories are copied recursively).</param>
    /// <param name="destinationDirectory">Existing target directory.</param>
    /// <param name="cancellationToken">Stops before the next source.</param>
    public static Task<KazoFileTransferResult> CopyAsync(
        IReadOnlyList<string> sources, string destinationDirectory, CancellationToken cancellationToken = default) =>
        TransferAsync(sources, destinationDirectory, KazoFileTransferMode.Copy, resolveConflict: null, cancellationToken);

    /// <summary>
    /// Moves sources into a directory. A source already in that directory is skipped (cut then pasted
    /// in place does nothing); a taken name asks <paramref name="resolveConflict"/>, or is skipped when
    /// none is supplied.
    /// </summary>
    /// <param name="sources">Absolute paths of files and directories.</param>
    /// <param name="destinationDirectory">Existing target directory.</param>
    /// <param name="resolveConflict">Asks the user; <see langword="null"/> skips every conflict.</param>
    /// <param name="cancellationToken">Stops before the next source.</param>
    public static Task<KazoFileTransferResult> MoveAsync(
        IReadOnlyList<string> sources,
        string destinationDirectory,
        KazoFileConflictResolver? resolveConflict,
        CancellationToken cancellationToken = default) =>
        TransferAsync(sources, destinationDirectory, KazoFileTransferMode.Move, resolveConflict, cancellationToken);

    /// <summary>Copies or moves sources into a directory; see <see cref="CopyAsync"/> and <see cref="MoveAsync"/>.</summary>
    /// <param name="sources">Absolute paths of files and directories.</param>
    /// <param name="destinationDirectory">Existing target directory.</param>
    /// <param name="mode">Copy or move.</param>
    /// <param name="resolveConflict">Used by a move only.</param>
    /// <param name="cancellationToken">
    /// Stops before the next source (as does a stop request of the app's process, ADR 0069); the
    /// sources not done are reported as failures and <see cref="KazoFileTransferResult.Cancelled"/> is set.
    /// </param>
    public static async Task<KazoFileTransferResult> TransferAsync(
        IReadOnlyList<string> sources,
        string destinationDirectory,
        KazoFileTransferMode mode,
        KazoFileConflictResolver? resolveConflict,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        string destination = KazoPath.GetFullPath(destinationDirectory);
        Outcome outcome = new();

        HashSet<string> taken;
        try
        {
            taken = await ListNamesAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            foreach (string source in sources)
            {
                outcome.Fail(source, Text["Transfer.Error.CannotOpen", destination, Describe(exception)]);
            }

            return outcome.ToResult();
        }

        // Validation pass before any write: a folder never goes into itself or a descendant.
        List<string> valid = [];
        foreach (string rawSource in sources)
        {
            string source = KazoPath.GetFullPath(rawSource);
            if (destination == source || destination.StartsWith(source + "/", StringComparison.Ordinal))
            {
                outcome.Fail(source, mode == KazoFileTransferMode.Copy
                    ? Text["Transfer.Error.CopyIntoItself"]
                    : Text["Transfer.Error.MoveIntoItself"]);
            }
            else
            {
                valid.Add(source);
            }
        }

        KazoFileConflictResolution? appliedToAll = null;
        for (int index = 0; index < valid.Count; index++)
        {
            string source = valid[index];
            if (!await ContinueAsync(valid, index, static path => path, outcome, cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            try
            {
                bool sourceIsDirectory = await KazoDirectory.ExistsAsync(source, cancellationToken).ConfigureAwait(false);
                if (!sourceIsDirectory && !await KazoFile.ExistsAsync(source, cancellationToken).ConfigureAwait(false))
                {
                    outcome.Fail(source, Text["Transfer.Error.NoLongerExists"]);
                    continue;
                }

                string name = GetName(source);
                string? parent = KazoPath.GetDirectoryName(source);
                if (mode == KazoFileTransferMode.Move && parent == destination)
                {
                    outcome.Skipped.Add(source);
                    continue;
                }

                string target = Combine(destination, name);
                bool replace = false;
                if (taken.Contains(name))
                {
                    if (mode == KazoFileTransferMode.Copy)
                    {
                        target = Combine(destination, GetCopyName(name, sourceIsDirectory, taken.Contains));
                    }
                    else
                    {
                        bool destinationIsDirectory = await KazoDirectory.ExistsAsync(target, cancellationToken).ConfigureAwait(false);
                        KazoFileConflictResolution resolution = appliedToAll
                            ?? (resolveConflict is null
                                ? new KazoFileConflictResolution(KazoFileConflictChoice.Skip)
                                : await resolveConflict(new KazoFileTransferConflict(source, target, sourceIsDirectory, destinationIsDirectory)).ConfigureAwait(false));
                        if (resolution.ApplyToAll)
                        {
                            appliedToAll = resolution;
                        }

                        switch (resolution.Choice)
                        {
                            case KazoFileConflictChoice.Skip:
                                outcome.Skipped.Add(source);
                                continue;
                            case KazoFileConflictChoice.KeepBoth:
                                target = Combine(destination, GetCopyName(name, sourceIsDirectory, taken.Contains));
                                break;
                            default:
                                replace = true;
                                break;
                        }
                    }
                }

                if (replace)
                {
                    await DeleteAsync(target, cancellationToken).ConfigureAwait(false);
                }

                await TransferOneAsync(source, target, sourceIsDirectory, mode, cancellationToken).ConfigureAwait(false);
                taken.Add(GetName(target));
                outcome.Completed.Add(new KazoFileTransferItemResult(source, target));
            }
            catch (Exception exception) when (IsFileError(exception))
            {
                outcome.Fail(source, Describe(exception));
            }
        }

        return outcome.ToResult();
    }

    /// <summary>
    /// Writes files dropped from the host system into a directory, like a copy: a taken name gets a
    /// <c>(copy)</c> suffix.
    /// </summary>
    /// <param name="files">Host files.</param>
    /// <param name="destinationDirectory">Existing target directory.</param>
    /// <param name="cancellationToken">Stops before the next file, like a stop request of the app (ADR 0069).</param>
    public static async Task<KazoFileTransferResult> ImportAsync(
        IReadOnlyList<KazoHostFile> files, string destinationDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        string destination = KazoPath.GetFullPath(destinationDirectory);
        Outcome outcome = new();

        HashSet<string> taken;
        try
        {
            taken = await ListNamesAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            foreach (KazoHostFile file in files)
            {
                outcome.Fail(file.Name, Text["Transfer.Error.CannotOpen", destination, Describe(exception)]);
            }

            return outcome.ToResult();
        }

        for (int index = 0; index < files.Count; index++)
        {
            KazoHostFile file = files[index];
            if (!await ContinueAsync(files, index, static host => host.Name, outcome, cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(file.Name) || file.Name.Contains('/', StringComparison.Ordinal) || file.Name is "." or "..")
            {
                outcome.Fail(file.Name, Text["Transfer.Error.InvalidName"]);
                continue;
            }

            if (file.Size > MaxHostFileBytes)
            {
                outcome.Fail(file.Name, Text["Transfer.Error.TooLarge", MaxHostFileBytes / (1024 * 1024)]);
                continue;
            }

            string name = taken.Contains(file.Name) ? GetCopyName(file.Name, isDirectory: false, taken.Contains) : file.Name;
            string target = Combine(destination, name);
            try
            {
                using MemoryStream buffer = new();
                await using (Stream content = await file.OpenReadStreamAsync(MaxHostFileBytes, cancellationToken).ConfigureAwait(false))
                {
                    await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                }

                await KazoFile.WriteAllBytesAsync(target, buffer.ToArray(), cancellationToken).ConfigureAwait(false);
                taken.Add(name);
                outcome.Completed.Add(new KazoFileTransferItemResult(file.Name, target));
            }
            catch (Exception exception) when (IsFileError(exception))
            {
                outcome.Fail(file.Name, Describe(exception));
            }
        }

        return outcome.ToResult();
    }

    /// <summary>Moves sources to the trash (ADR 0052), as a drop on the Trash view does.</summary>
    /// <param name="sources">Absolute paths.</param>
    /// <param name="cancellationToken">Stops before the next source, like a stop request of the app (ADR 0069).</param>
    public static async Task<KazoFileTransferResult> MoveToTrashAsync(IReadOnlyList<string> sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        Outcome outcome = new();
        for (int index = 0; index < sources.Count; index++)
        {
            if (!await ContinueAsync(sources, index, static path => path, outcome, cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            string source = KazoPath.GetFullPath(sources[index]);
            try
            {
                KazoTrashItem item = await KazoTrash.MoveToTrashAsync(source, cancellationToken).ConfigureAwait(false);
                outcome.Completed.Add(new KazoFileTransferItemResult(source, KazoPath.Combine(KazoTrash.TrashDirectory, "files", item.Name)));
            }
            catch (Exception exception) when (IsFileError(exception))
            {
                outcome.Fail(source, Describe(exception));
            }
        }

        return outcome.ToResult();
    }

    /// <summary>
    /// Gets the first free Nautilus-style copy name: <c>report.txt</c> → <c>report (copy).txt</c>, then
    /// <c>report (copy 2).txt</c>… A name that already ends with such a suffix continues the series
    /// (<c>report (copy).txt</c> → <c>report (copy 2).txt</c>). The extension of a directory or a dot
    /// file (<c>.bashrc</c>) is not split off.
    /// </summary>
    /// <param name="name">Name that is taken.</param>
    /// <param name="isDirectory">Whether the entry is a directory.</param>
    /// <param name="isTaken">Tells whether a candidate name is already used in the destination.</param>
    public static string GetCopyName(string name, bool isDirectory, Func<string, bool> isTaken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(isTaken);

        int dot = isDirectory ? -1 : name.LastIndexOf('.');
        string stem = dot > 0 ? name[..dot] : name;
        string extension = dot > 0 ? name[dot..] : string.Empty;

        int next = 1;
        System.Text.RegularExpressions.Match suffix = System.Text.RegularExpressions.Regex.Match(stem, @"^(?<base>.+) \(copy(?: (?<n>\d+))?\)$");
        if (suffix.Success)
        {
            stem = suffix.Groups["base"].Value;
            next = suffix.Groups["n"].Success && int.TryParse(suffix.Groups["n"].Value, out int n) ? n + 1 : 2;
        }

        for (int index = next; ; index++)
        {
            string candidate = index == 1 ? $"{stem} (copy){extension}" : $"{stem} (copy {index}){extension}";
            if (!isTaken(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Gets the last segment of a path (the name of <c>/</c> is <c>/</c>).</summary>
    /// <param name="path">Path or bare name.</param>
    public static string GetName(string path)
    {
        string trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
        int slash = trimmed.LastIndexOf('/');
        return slash < 0 || trimmed.Length == 1 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>
    /// The checkpoint between two items (ADR 0069): gives the UI the thread back and, when the app
    /// is asked to stop (or <paramref name="cancellationToken"/> is cancelled), ends the operation
    /// cleanly. What is done stays done; the items not reached are reported as failures, never lost
    /// silently.
    /// </summary>
    /// <returns><see langword="false"/> when the operation must stop at <paramref name="index"/>.</returns>
    private static async Task<bool> ContinueAsync<T>(
        IReadOnlyList<T> items, int index, Func<T, string> name, Outcome outcome, CancellationToken cancellationToken)
    {
        try
        {
            await KazoProcess.CheckpointAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            for (int remaining = index; remaining < items.Count; remaining++)
            {
                outcome.Fail(name(items[remaining]), Text["Transfer.Error.Stopped"]);
            }

            outcome.Cancelled = true;
            return false;
        }
    }

    private static async Task TransferOneAsync(
        string source, string target, bool sourceIsDirectory, KazoFileTransferMode mode, CancellationToken cancellationToken)
    {
        switch (mode, sourceIsDirectory)
        {
            case (KazoFileTransferMode.Copy, true):
                await KazoDirectory.CopyAsync(source, target, cancellationToken).ConfigureAwait(false);
                break;
            case (KazoFileTransferMode.Copy, false):
                await KazoFile.CopyAsync(source, target, overwrite: false, cancellationToken).ConfigureAwait(false);
                break;
            case (_, true):
                await KazoDirectory.MoveAsync(source, target, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await KazoFile.MoveAsync(source, target, overwrite: false, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private static async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        if (await KazoDirectory.ExistsAsync(path, cancellationToken).ConfigureAwait(false))
        {
            await KazoDirectory.DeleteAsync(path, recursive: true, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await KazoFile.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<HashSet<string>> ListNamesAsync(string directory, CancellationToken cancellationToken)
    {
        string[] entries = await KazoDirectory.GetFileSystemEntriesAsync(directory, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new HashSet<string>(entries.Select(GetName), StringComparer.Ordinal);
    }

    private static string Combine(string directory, string name) => directory == "/" ? "/" + name : $"{directory}/{name}";

    private static bool IsFileError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    /// <summary>Turns a file error into the short reason of the summary.</summary>
    private static string Describe(Exception exception)
    {
        FileSystemErrorCode? code = exception switch
        {
            KazoUnauthorizedAccessException denied => denied.ErrorCode,
            KazoIOException io => io.ErrorCode,
            _ => null
        };

        Microsoft.Extensions.Localization.IStringLocalizer<Strings> text = Text;
        return code switch
        {
            FileSystemErrorCode.PermissionDenied or FileSystemErrorCode.AuthorityDenied => text["Transfer.Error.PermissionDenied"],
            FileSystemErrorCode.CapabilityDenied => text["Transfer.Error.AppNotAllowed"],
            FileSystemErrorCode.UnsupportedOperation => text["Transfer.Error.ReadOnly"],
            FileSystemErrorCode.ProtectedEntry => text["Transfer.Error.Protected"],
            FileSystemErrorCode.NotFound => text["Transfer.Error.NoLongerExists"],
            FileSystemErrorCode.AlreadyExists => text["Transfer.Error.NameTaken"],
            _ when exception is UnauthorizedAccessException => text["Transfer.Error.PermissionDenied"],
            _ => exception.Message
        };
    }

    /// <summary>Accumulates one operation's outcome.</summary>
    private sealed class Outcome
    {
        public List<KazoFileTransferItemResult> Completed { get; } = [];

        public List<string> Skipped { get; } = [];

        private List<KazoFileTransferFailure> Failures { get; } = [];

        public bool Cancelled { get; set; }

        public void Fail(string path, string reason) => Failures.Add(new KazoFileTransferFailure(path, reason));

        public KazoFileTransferResult ToResult() => new(Completed, Skipped, Failures, Cancelled);
    }
}
