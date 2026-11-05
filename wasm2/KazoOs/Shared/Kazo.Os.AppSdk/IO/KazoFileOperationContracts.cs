namespace Kazo.Os.AppSdk.IO;

/// <summary>What a paste or a drop does with its sources (ADR 0058, section 1).</summary>
public enum KazoFileTransferMode
{
    /// <summary>Duplicate the sources; a name already taken gets a <c>(copy)</c> suffix.</summary>
    Copy = 0,

    /// <summary>Move the sources; a name already taken asks <see cref="KazoFileConflictChoice"/>.</summary>
    Move = 1
}

/// <summary>Answer to a name conflict while moving (the "Replace / Keep both / Skip" window).</summary>
public enum KazoFileConflictChoice
{
    /// <summary>Delete what is at the destination, then move the source there.</summary>
    Replace = 0,

    /// <summary>Move the source under a free <c>(copy)</c> name, keeping both.</summary>
    KeepBoth = 1,

    /// <summary>Leave this source where it is.</summary>
    Skip = 2
}

/// <summary>The user's answer to one conflict.</summary>
/// <param name="Choice">What to do with this source.</param>
/// <param name="ApplyToAll">Whether the same answer goes for every later conflict of this transfer, without asking.</param>
public sealed record KazoFileConflictResolution(KazoFileConflictChoice Choice, bool ApplyToAll = false);

/// <summary>One name conflict a move ran into.</summary>
/// <param name="SourcePath">Entry being moved.</param>
/// <param name="DestinationPath">Path already taken.</param>
/// <param name="SourceIsDirectory">Whether the source is a directory.</param>
/// <param name="DestinationIsDirectory">Whether what occupies the destination is a directory.</param>
public sealed record KazoFileTransferConflict(string SourcePath, string DestinationPath, bool SourceIsDirectory, bool DestinationIsDirectory)
{
    /// <summary>Gets the conflicting name.</summary>
    public string Name => KazoFileOperations.GetName(DestinationPath);
}

/// <summary>Asks the user how to settle a name conflict.</summary>
/// <param name="conflict">The conflict.</param>
public delegate Task<KazoFileConflictResolution> KazoFileConflictResolver(KazoFileTransferConflict conflict);

/// <summary>One source that could not be transferred, and why, for the end-of-operation summary.</summary>
/// <param name="Path">Source path (or host file name).</param>
/// <param name="Reason">Short reason, in English like the rest of the UI ("permission denied", "read-only file system"…).</param>
public sealed record KazoFileTransferFailure(string Path, string Reason);

/// <summary>One source that reached its destination.</summary>
/// <param name="SourcePath">Original path (or host file name).</param>
/// <param name="DestinationPath">Where it is now.</param>
public sealed record KazoFileTransferItemResult(string SourcePath, string DestinationPath);

/// <summary>Outcome of a whole paste, drop, import or trash operation: an error on one source never stops the others.</summary>
/// <param name="Completed">Sources transferred, in order.</param>
/// <param name="Skipped">Sources left alone: answered "Skip", or cut and pasted into their own folder.</param>
/// <param name="Failures">Sources that failed, with the reason.</param>
/// <param name="Cancelled">Whether the operation stopped early (a stop request of the app, ADR 0069): the sources not done are among <paramref name="Failures"/>.</param>
public sealed record KazoFileTransferResult(
    IReadOnlyList<KazoFileTransferItemResult> Completed,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<KazoFileTransferFailure> Failures,
    bool Cancelled = false)
{
    /// <summary>Gets whether no source failed.</summary>
    public bool Succeeded => Failures.Count == 0;

    /// <summary>
    /// Builds the one-paragraph summary of the failures, or <see langword="null"/> when there are
    /// none, in the language of the ambient app's process (ADR 0104).
    /// </summary>
    /// <param name="verb">
    /// Verb of the operation: <c>"copy"</c>, <c>"move"</c>, <c>"move to the trash"</c> and
    /// <c>"upload"</c> are translated; any other verb is printed as given.
    /// </param>
    public string? DescribeFailures(string verb) => DescribeFailures(verb, Kazo.Os.AppSdk.Globalization.KazoLocalization.GetLocalizer<Kazo.Os.AppSdk.Strings>());

    /// <summary>Builds the summary of the failures in the language of <paramref name="context"/>'s process.</summary>
    /// <param name="verb">Verb of the operation (see <see cref="DescribeFailures(string)"/>).</param>
    /// <param name="context">The app whose language the summary is written in.</param>
    public string? DescribeFailures(string verb, IAppExecutionContext? context) =>
        DescribeFailures(verb, Kazo.Os.AppSdk.Globalization.KazoLocalization.GetLocalizer<Kazo.Os.AppSdk.Strings>(context));

    private string? DescribeFailures(string verb, Microsoft.Extensions.Localization.IStringLocalizer<Kazo.Os.AppSdk.Strings> text)
    {
        ArgumentNullException.ThrowIfNull(verb);
        if (Failures.Count == 0)
        {
            return null;
        }

        string localizedVerb = verb switch
        {
            "copy" => text["Transfer.Verb.Copy"],
            "move" => text["Transfer.Verb.Move"],
            "move to the trash" => text["Transfer.Verb.Trash"],
            "upload" => text["Transfer.Verb.Upload"],
            _ => verb
        };
        string items = string.Join("; ", Failures.Select(failure => (string)text["Transfer.Failure.Item", KazoFileOperations.GetName(failure.Path), failure.Reason]));
        return Failures.Count == 1
            ? text["Transfer.Failure.One", localizedVerb, items]
            : text["Transfer.Failure.Many", localizedVerb, Failures.Count, items];
    }
}

/// <summary>
/// A file dropped from the host system (the user's real desktop), read through the browser. Its
/// content is only read when the transfer reaches it.
/// </summary>
/// <param name="Name">File name as the host gave it.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="ContentType">MIME type the browser guessed, possibly empty.</param>
/// <param name="OpenReadStreamAsync">Opens the content; the argument caps the bytes read.</param>
public sealed record KazoHostFile(
    string Name,
    long Size,
    string? ContentType,
    Func<long, CancellationToken, Task<Stream>> OpenReadStreamAsync);
