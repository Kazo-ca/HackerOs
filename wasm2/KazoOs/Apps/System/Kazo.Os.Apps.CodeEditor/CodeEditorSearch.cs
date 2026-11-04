using System.Text.RegularExpressions;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.Threading;
using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Apps.CodeEditor;

/// <summary>What the Search view looks for (VS Code's search box and its three toggles).</summary>
/// <param name="Text">The text or pattern typed.</param>
/// <param name="MatchCase">Aa: case-sensitive.</param>
/// <param name="WholeWord">ab: the match must not continue a word on either side.</param>
/// <param name="UseRegex">.*: <paramref name="Text"/> is a .NET regular expression.</param>
/// <param name="Include">"files to include": comma-separated globs, empty for every file.</param>
/// <param name="Exclude">"files to exclude": comma-separated globs.</param>
public sealed record CodeEditorSearchQuery(
    string Text,
    bool MatchCase = false,
    bool WholeWord = false,
    bool UseRegex = false,
    string? Include = null,
    string? Exclude = null);

/// <summary>One match: 1-based line and column, length in characters, and the line's text for the preview.</summary>
/// <param name="Line">1-based line.</param>
/// <param name="Column">1-based column of the first matched character.</param>
/// <param name="Length">Matched characters.</param>
/// <param name="LineText">The line as shown in the results (leading blanks removed, cut when long).</param>
/// <param name="PreviewOffset">Where the match starts inside <paramref name="LineText"/>, for highlighting.</param>
public sealed record CodeEditorSearchMatch(int Line, int Column, int Length, string LineText, int PreviewOffset);

/// <summary>The matches found in one file.</summary>
public sealed record CodeEditorSearchFileResult(CodeEditorWorkspaceFile File, IReadOnlyList<CodeEditorSearchMatch> Matches);

/// <summary>The outcome of a search across the open folder.</summary>
/// <param name="Files">Files with at least one match, in listing order.</param>
/// <param name="MatchCount">Matches in all files.</param>
/// <param name="FilesSearched">Files read and searched.</param>
/// <param name="Truncated">Whether a limit stopped the search (files listed, matches kept).</param>
/// <param name="Error">An invalid pattern, or null.</param>
public sealed record CodeEditorSearchResult(
    IReadOnlyList<CodeEditorSearchFileResult> Files,
    int MatchCount,
    int FilesSearched,
    bool Truncated,
    string? Error = null)
{
    /// <summary>An empty result (nothing typed).</summary>
    public static CodeEditorSearchResult Empty { get; } = new([], 0, 0, false);
}

/// <summary>
/// Text search, line by line like VS Code's (a match never spans lines), with the matcher built
/// once per query. Pure: <see cref="CodeEditorWorkspaceSearch"/> feeds it the files.
/// </summary>
public static class CodeEditorTextSearch
{
    /// <summary>Most matches kept for one file.</summary>
    public const int MaxMatchesPerFile = 1000;

    /// <summary>Longest line preview kept; the preview is cut around the match beyond it.</summary>
    public const int MaxPreviewLength = 240;

    /// <summary>Builds the matcher for <paramref name="query"/>, or returns the error of an invalid pattern.</summary>
    public static Regex? BuildMatcher(CodeEditorSearchQuery query, out string? error)
    {
        ArgumentNullException.ThrowIfNull(query);
        error = null;
        if (string.IsNullOrEmpty(query.Text))
        {
            return null;
        }

        string pattern = query.UseRegex ? query.Text : Regex.Escape(query.Text);
        if (query.WholeWord)
        {
            // \b fails next to punctuation ("\bfoo(\b"); look-arounds on word characters do not.
            pattern = $"(?<![\\w]){pattern}(?![\\w])";
        }

        RegexOptions options = RegexOptions.CultureInvariant | (query.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            return new Regex(pattern, options, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException exception)
        {
            error = Strings.Ambient["Search.InvalidRegex", exception.Message];
            return null;
        }
    }

    /// <summary>Finds the matches of <paramref name="matcher"/> in <paramref name="content"/>, line by line.</summary>
    public static IReadOnlyList<CodeEditorSearchMatch> Search(string content, Regex matcher)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(matcher);
        List<CodeEditorSearchMatch> matches = [];
        int lineNumber = 0;
        int start = 0;
        while (start <= content.Length && matches.Count < MaxMatchesPerFile)
        {
            int end = content.IndexOf('\n', start);
            int lineEnd = end < 0 ? content.Length : end;
            string line = content[start..lineEnd].TrimEnd('\r');
            lineNumber++;
            try
            {
                foreach (Match match in matcher.Matches(line))
                {
                    // An empty match (e.g. "^" or "a*") would show nothing: skip it.
                    if (match.Length == 0)
                    {
                        continue;
                    }

                    (string preview, int offset) = Preview(line, match.Index);
                    matches.Add(new CodeEditorSearchMatch(lineNumber, match.Index + 1, match.Length, preview, offset));
                    if (matches.Count >= MaxMatchesPerFile)
                    {
                        break;
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological pattern on this line: skip the line rather than freeze the editor.
            }

            if (end < 0)
            {
                break;
            }

            start = end + 1;
        }

        return matches;
    }

    /// <summary>Gets the line shown for a match, cut around it when long, and where the match starts in it.</summary>
    private static (string Text, int Offset) Preview(string line, int matchIndex)
    {
        string trimmed = line.TrimStart();
        int removed = line.Length - trimmed.Length;
        int offset = Math.Max(0, matchIndex - removed);
        if (trimmed.Length <= MaxPreviewLength)
        {
            return (trimmed, offset);
        }

        int from = Math.Max(0, offset - 40);
        int length = Math.Min(MaxPreviewLength, trimmed.Length - from);
        string prefix = from > 0 ? "\u2026" : string.Empty;
        return (prefix + trimmed.Substring(from, length), offset - from + prefix.Length);
    }
}

/// <summary>
/// The "files to include / exclude" globs of the Search view: comma-separated, <c>*</c> and
/// <c>?</c> inside a segment, <c>**</c> across folders. A glob without <c>/</c> matches the file
/// name anywhere (<c>*.cs</c>); one with <c>/</c> matches the relative path (<c>src/**</c>).
/// </summary>
public sealed class CodeEditorGlob
{
    private readonly IReadOnlyList<(Regex Pattern, bool MatchesName)> _patterns;

    private CodeEditorGlob(IReadOnlyList<(Regex, bool)> patterns) => _patterns = patterns;

    /// <summary>Gets whether no glob was given.</summary>
    public bool IsEmpty => _patterns.Count == 0;

    /// <summary>Parses a comma-separated list of globs; blank entries are ignored.</summary>
    public static CodeEditorGlob Parse(string? globs)
    {
        List<(Regex, bool)> patterns = [];
        foreach (string raw in (globs ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string glob = raw.StartsWith("./", StringComparison.Ordinal) ? raw[2..] : raw;
            bool matchesName = !glob.Contains('/');
            // A folder name alone ("src/") means everything under it.
            if (glob.EndsWith('/'))
            {
                glob += "**";
            }

            patterns.Add((new Regex("^" + ToRegex(glob) + "$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), matchesName));
        }

        return new CodeEditorGlob(patterns);
    }

    /// <summary>Gets whether any glob matches the file.</summary>
    public bool Matches(CodeEditorWorkspaceFile file) =>
        _patterns.Any(entry => entry.MatchesName
            ? entry.Pattern.IsMatch(file.Name) || file.RelativePath.Split('/').SkipLast(1).Any(entry.Pattern.IsMatch)
            : entry.Pattern.IsMatch(file.RelativePath));

    private static string ToRegex(string glob)
    {
        System.Text.StringBuilder pattern = new();
        for (int index = 0; index < glob.Length; index++)
        {
            char current = glob[index];
            if (current == '*' && index + 1 < glob.Length && glob[index + 1] == '*')
            {
                // "**/" matches zero or more folders; a trailing "**" anything.
                bool slash = index + 2 < glob.Length && glob[index + 2] == '/';
                pattern.Append(slash ? "(?:.*/)?" : ".*");
                index += slash ? 2 : 1;
            }
            else if (current == '*')
            {
                pattern.Append("[^/]*");
            }
            else if (current == '?')
            {
                pattern.Append("[^/]");
            }
            else
            {
                pattern.Append(Regex.Escape(current.ToString()));
            }
        }

        return pattern.ToString();
    }
}

/// <summary>
/// Searches every text file of the open folder (Ctrl+Shift+F). Open tabs are searched in their
/// current, possibly unsaved, content, as VS Code does; binary and oversized files are skipped.
/// </summary>
public sealed class CodeEditorWorkspaceSearch
{
    /// <summary>Most matches kept in total; the search stops beyond.</summary>
    public const int MaxTotalMatches = 5000;

    private readonly CodeEditorWorkspace _workspace;
    private readonly CodeEditorFileService _files;

    /// <summary>Creates the search over <paramref name="workspace"/>.</summary>
    public CodeEditorWorkspaceSearch(CodeEditorWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _files = new CodeEditorFileService(workspace.FileSystem);
    }

    /// <summary>Runs <paramref name="query"/> over the folder, on the calling thread.</summary>
    /// <param name="query">What to look for.</param>
    /// <param name="openContent">The content of an open tab for a path, or null to read the file.</param>
    /// <param name="cancellationToken">Cancels a search superseded by new typing.</param>
    public async Task<CodeEditorSearchResult> SearchAsync(
        CodeEditorSearchQuery query,
        Func<VirtualPath, string?>? openContent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        Regex? matcher = CodeEditorTextSearch.BuildMatcher(query, out string? error);
        if (matcher is null)
        {
            return error is null ? CodeEditorSearchResult.Empty : CodeEditorSearchResult.Empty with { Error = error };
        }

        CodeEditorWorkspaceListing listing = await _workspace.ListFilesAsync(cancellationToken);
        // ADR 0069: a folder-wide search is long work on the UI thread; let the UI paint and honor a
        // stop request between files.
        return await SearchFilesAsync(
            listing, query, matcher, openContent, _files,
            () => HackProcess.CheckpointAsync(cancellationToken), progress: null, cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="query"/> over the folder in a background task (ADR 0128, ADR 0140): the
    /// folder is listed here, then the files are read and matched by a child process of the editor
    /// (<c>code:search</c> in <c>ps</c>), in a Web Worker when the host has some, so a large folder or
    /// a slow pattern never freezes the desktop. Cancelling (new typing) cancels the task.
    /// </summary>
    /// <param name="query">What to look for.</param>
    /// <param name="openContent">The content of an open tab for a path, or null to read the file.</param>
    /// <param name="progress">Receives the share of files searched, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels a search superseded by new typing.</param>
    internal async Task<CodeEditorSearchResult> SearchInBackgroundAsync(
        CodeEditorSearchQuery query,
        Func<VirtualPath, string?>? openContent,
        IProgress<HackBackgroundProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        // An invalid pattern is reported at once, without starting a process.
        if (CodeEditorTextSearch.BuildMatcher(query, out string? error) is null)
        {
            return error is null ? CodeEditorSearchResult.Empty : CodeEditorSearchResult.Empty with { Error = error };
        }

        CodeEditorWorkspaceListing listing = await _workspace.ListFilesAsync(cancellationToken);
        // The task sees nothing of the window: the files cross as paths, unsaved tabs as their text.
        List<CodeEditorSearchJobFile> files = [.. listing.Files.Select(file =>
            new CodeEditorSearchJobFile(file.Path.Value, file.RelativePath, file.Length, openContent?.Invoke(file.Path)))];
        CodeEditorSearchJobOutput output = await HackBackground.RunAsync<CodeEditorSearchJobInput, CodeEditorSearchJobOutput>(
            CodeEditorSearchJobs.Search,
            new CodeEditorSearchJobInput(query, files, listing.Truncated),
            progress,
            new HackBackgroundOptions { Name = "search" },
            cancellationToken);
        return new CodeEditorSearchResult(
            [.. output.Files.Select(found => new CodeEditorSearchFileResult(listing.Files[found.FileIndex], found.Matches))],
            output.MatchCount,
            output.FilesSearched,
            output.Truncated);
    }

    /// <summary>
    /// The search proper, shared by <see cref="SearchAsync"/> (on the calling thread) and
    /// <see cref="CodeEditorSearchJobs.Search"/> (in the background task).
    /// </summary>
    internal static async Task<CodeEditorSearchResult> SearchFilesAsync(
        CodeEditorWorkspaceListing listing,
        CodeEditorSearchQuery query,
        Regex matcher,
        Func<VirtualPath, string?>? openContent,
        CodeEditorFileService fileService,
        Func<ValueTask> checkpoint,
        Action<int>? progress,
        CancellationToken cancellationToken)
    {
        CodeEditorGlob include = CodeEditorGlob.Parse(query.Include);
        CodeEditorGlob exclude = CodeEditorGlob.Parse(query.Exclude);
        List<CodeEditorSearchFileResult> results = [];
        int total = 0;
        int searched = 0;
        int visited = 0;
        bool truncated = listing.Truncated;
        foreach (CodeEditorWorkspaceFile file in listing.Files)
        {
            await checkpoint();
            progress?.Invoke(visited++);
            if ((!include.IsEmpty && !include.Matches(file)) || exclude.Matches(file))
            {
                continue;
            }

            string? content = openContent?.Invoke(file.Path);
            if (content is null)
            {
                if (file.Length > CodeEditorDocument.MaxDocumentBytes)
                {
                    continue;
                }

                CodeEditorLoadResult loaded = await fileService.LoadAsync(file.Path, cancellationToken);
                if (!loaded.Succeeded || loaded.Content is null)
                {
                    continue;
                }

                content = loaded.Content;
            }

            searched++;
            IReadOnlyList<CodeEditorSearchMatch> matches = CodeEditorTextSearch.Search(content, matcher);
            if (matches.Count == 0)
            {
                continue;
            }

            results.Add(new CodeEditorSearchFileResult(file, matches));
            total += matches.Count;
            if (total >= MaxTotalMatches)
            {
                truncated = true;
                break;
            }
        }

        return new CodeEditorSearchResult(results, total, searched, truncated);
    }
}

/// <summary>
/// Quick open (Ctrl+P): ranks the folder's files against what is typed, as a fuzzy subsequence
/// (<c>edwin</c> finds <c>EditorWindow.razor</c>), preferring matches in the file name, at word
/// starts and in a row.
/// </summary>
public static class CodeEditorQuickOpen
{
    /// <summary>Most entries returned.</summary>
    public const int MaxResults = 50;

    /// <summary>Gets the files matching <paramref name="text"/>, best first; every file (by path) when nothing is typed.</summary>
    public static IReadOnlyList<CodeEditorWorkspaceFile> Filter(IReadOnlyList<CodeEditorWorkspaceFile> files, string? text)
    {
        ArgumentNullException.ThrowIfNull(files);
        string query = (text ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (query.Length == 0)
        {
            return files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Take(MaxResults).ToArray();
        }

        return files
            .Select(file => (File: file, Score: Score(file, query)))
            .Where(entry => entry.Score > 0)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.File.RelativePath.Length)
            .ThenBy(entry => entry.File.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(MaxResults)
            .Select(entry => entry.File)
            .ToArray();
    }

    /// <summary>Scores a file: 0 when the query is not a subsequence of its path.</summary>
    public static int Score(CodeEditorWorkspaceFile file, string query)
    {
        ArgumentNullException.ThrowIfNull(file);
        int nameScore = SubsequenceScore(file.Name, query);
        if (nameScore > 0)
        {
            return 1000 + nameScore;
        }

        return SubsequenceScore(file.RelativePath, query);
    }

    private static int SubsequenceScore(string candidate, string query)
    {
        int score = 0;
        int position = 0;
        int previous = -2;
        foreach (char wanted in query)
        {
            int found = -1;
            for (int index = position; index < candidate.Length; index++)
            {
                if (char.ToLowerInvariant(candidate[index]) == char.ToLowerInvariant(wanted))
                {
                    found = index;
                    break;
                }
            }

            if (found < 0)
            {
                return 0;
            }

            score += 1;
            if (found == previous + 1)
            {
                score += 5;
            }

            if (found == 0 || candidate[found - 1] is '/' or '.' or '-' or '_' || (char.IsUpper(candidate[found]) && char.IsLower(candidate[found - 1])))
            {
                score += 8;
            }

            previous = found;
            position = found + 1;
        }

        return score;
    }
}
