using Kazo.Os.AppSdk.IO;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.FileExplorer;

/// <summary>What a transfer was doing, for its failure summary (<c>Transfer.&lt;verb&gt;.One/Many</c>).</summary>
internal enum TransferVerb
{
    Copy,
    Move,
    Trash,
    Upload
}

/// <summary>
/// The end-of-operation summary of a transfer, shared by the explorer window (uploads, drops on the
/// Trash) and the file control it provides (pastes, drops, deletions).
/// </summary>
internal static class ExplorerTransferReport
{
    /// <summary>
    /// The failure summary in the explorer's texts, worded like
    /// <see cref="HackFileTransferResult.DescribeFailures(string)"/>: <c>Could not copy 'a': reason.</c>, or
    /// the count and the list when several items failed; <see langword="null"/> when nothing failed.
    /// </summary>
    /// <param name="text">The explorer's texts in the language of the app that shows them.</param>
    /// <param name="result">The transfer's outcome.</param>
    /// <param name="verb">What the transfer was doing.</param>
    internal static string? DescribeFailures(IStringLocalizer<Strings> text, HackFileTransferResult result, TransferVerb verb)
    {
        if (result.Failures.Count == 0)
        {
            return null;
        }

        string items = string.Join("; ", result.Failures.Select(failure => text["Error.Item", HackFileOperations.GetName(failure.Path), failure.Reason].Value));
        return result.Failures.Count == 1
            ? text[$"Transfer.{verb}.One", items]
            : text[$"Transfer.{verb}.Many", result.Failures.Count, items];
    }
}
