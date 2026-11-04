using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Threading;
using Kazo.Os.Apps.CodeEditor;

// ADR 0128 section 2: the search task's input and result cross as JSON, through this context.
[assembly: HackWireContext(typeof(CodeEditorJobsJsonContext))]

namespace Kazo.Os.Apps.CodeEditor;

/// <summary>One file of the folder as the search task receives it: its path as text, and an unsaved tab's content.</summary>
internal sealed record CodeEditorSearchJobFile(string Path, string RelativePath, long Length, string? OpenContent);

/// <summary>What the search task searches: the query, the listed files, and whether the listing stopped at its limit.</summary>
internal sealed record CodeEditorSearchJobInput(CodeEditorSearchQuery Query, List<CodeEditorSearchJobFile> Files, bool ListingTruncated);

/// <summary>The matches of one file, by its index in <see cref="CodeEditorSearchJobInput.Files"/>.</summary>
internal sealed record CodeEditorSearchJobFileMatches(int FileIndex, List<CodeEditorSearchMatch> Matches);

/// <summary>What the search task found.</summary>
internal sealed record CodeEditorSearchJobOutput(List<CodeEditorSearchJobFileMatches> Files, int MatchCount, int FilesSearched, bool Truncated);

/// <summary>The serializer context of the editor's background tasks (ADR 0128, ADR 0124 section 2).</summary>
[JsonSerializable(typeof(CodeEditorSearchJobInput))]
[JsonSerializable(typeof(CodeEditorSearchJobOutput))]
internal sealed partial class CodeEditorJobsJsonContext : JsonSerializerContext;

/// <summary>The editor's background tasks (ADR 0128): static methods the OS may run in a Web Worker.</summary>
internal static class CodeEditorSearchJobs
{
    /// <summary>
    /// The folder-wide search (Ctrl+Shift+F) as its own process: reads each file through the task's
    /// own gateways (same user, same capabilities as the editor) and matches it, line by line.
    /// </summary>
    /// <param name="input">The query and the files.</param>
    /// <param name="context">Progress (share of files visited), cancellation, checkpoints.</param>
    /// <returns>The matches, by file index.</returns>
    public static async Task<CodeEditorSearchJobOutput> Search(CodeEditorSearchJobInput input, HackBackgroundContext context)
    {
        // The window already checked the pattern; an invalid one here means nothing to find.
        Regex? matcher = CodeEditorTextSearch.BuildMatcher(input.Query, out _);
        if (matcher is null)
        {
            return new CodeEditorSearchJobOutput([], 0, 0, false);
        }

        List<CodeEditorWorkspaceFile> files = [.. input.Files.Select(static file =>
            new CodeEditorWorkspaceFile(VirtualPath.Parse(file.Path), file.RelativePath, file.Length))];
        Dictionary<string, string> open = input.Files
            .Where(static file => file.OpenContent is not null)
            .ToDictionary(static file => file.Path, static file => file.OpenContent!, StringComparer.Ordinal);
        int count = Math.Max(1, files.Count);
        CodeEditorSearchResult result = await CodeEditorWorkspaceSearch.SearchFilesAsync(
            new CodeEditorWorkspaceListing(files, input.ListingTruncated),
            input.Query,
            matcher,
            path => open.TryGetValue(path.Value, out string? content) ? content : null,
            new CodeEditorFileService(HackExecutionContext.Current.FileSystem),
            context.CheckpointAsync,
            visited => context.Report((double)visited / count),
            context.CancellationToken);
        context.Report(1);

        Dictionary<CodeEditorWorkspaceFile, int> index = files.Select(static (file, position) => (file, position))
            .ToDictionary(static entry => entry.file, static entry => entry.position);
        return new CodeEditorSearchJobOutput(
            [.. result.Files.Select(found => new CodeEditorSearchJobFileMatches(index[found.File], [.. found.Matches]))],
            result.MatchCount,
            result.FilesSearched,
            result.Truncated);
    }
}
