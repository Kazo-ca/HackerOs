using System.Text;

namespace Kazo.Os.Apps.TextEditor;

/// <summary>
/// Search options shared by the find bar and the replace panel (ADR 0058 §3).
/// The default value (both flags off) is the historical behavior: case-insensitive,
/// substring matching. Regular expressions are deliberately not supported in phase 4.
/// </summary>
/// <param name="MatchCase">When <see langword="true"/>, matching is ordinal and case-sensitive.</param>
/// <param name="WholeWord">
/// When <see langword="true"/>, an occurrence only counts if it is not glued to a word
/// character (letter, digit or underscore) on either side, like the "Whole word" toggle of
/// common desktop editors.
/// </param>
public readonly record struct TextEditorFindOptions(bool MatchCase = false, bool WholeWord = false);

/// <summary>
/// Outcome of a single <see cref="TextEditorFindEngine.Replace"/> step.
/// </summary>
/// <param name="Content">The document content after the step (unchanged when <paramref name="Replaced"/> is false).</param>
/// <param name="Replaced">Whether the selected occurrence was replaced.</param>
/// <param name="NextMatchStart">
/// Start offset, in <paramref name="Content"/>, of the occurrence to select next, or -1 when
/// the content no longer contains any occurrence.
/// </param>
public readonly record struct TextEditorReplaceResult(string Content, bool Replaced, int NextMatchStart);

/// <summary>
/// Outcome of <see cref="TextEditorFindEngine.ReplaceAll"/>.
/// </summary>
/// <param name="Content">The document content after every occurrence was replaced.</param>
/// <param name="Count">Number of occurrences replaced (0 leaves <paramref name="Content"/> untouched).</param>
public readonly record struct TextEditorReplaceAllResult(string Content, int Count);

/// <summary>
/// Pure, Blazor/JS-independent find-and-replace logic for the text editor, so matching,
/// cycling, wrap-around and replacement behavior can be verified without rendering the
/// component (ADR 0058 §3).
/// </summary>
public static class TextEditorFindEngine
{
    /// <summary>Finds every case-insensitive occurrence of <paramref name="query"/> in <paramref name="content"/>, in order.</summary>
    public static List<int> FindMatches(string content, string query) =>
        FindMatches(content, query, default);

    /// <summary>
    /// Finds every non-overlapping occurrence of <paramref name="query"/> in
    /// <paramref name="content"/>, in order, honoring <paramref name="options"/>.
    /// </summary>
    public static List<int> FindMatches(string content, string query, TextEditorFindOptions options)
    {
        List<int> indexes = [];
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(content))
        {
            return indexes;
        }

        StringComparison comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int idx = 0;
        while ((idx = content.IndexOf(query, idx, comparison)) >= 0)
        {
            if (!options.WholeWord || IsWholeWordAt(content, idx, query.Length))
            {
                indexes.Add(idx);
                idx += query.Length;
            }
            else
            {
                // A rejected candidate may overlap the next whole-word occurrence ("aaa" vs "aa"),
                // so only step one character forward.
                idx++;
            }
        }
        return indexes;
    }

    /// <summary>Advances to the next match index, wrapping around; -1 when there are no matches.</summary>
    public static int Next(int currentIndex, int matchCount) =>
        matchCount == 0 ? -1 : (currentIndex + 1) % matchCount;

    /// <summary>Moves to the previous match index, wrapping around; -1 when there are no matches.</summary>
    public static int Previous(int currentIndex, int matchCount) =>
        matchCount == 0 ? -1 : (currentIndex - 1 + matchCount) % matchCount;

    /// <summary>
    /// Returns the start of the first occurrence at or after <paramref name="fromOffset"/>,
    /// wrapping to the first occurrence of the document; -1 when there is none.
    /// </summary>
    public static int FindNextFrom(string content, string query, int fromOffset, TextEditorFindOptions options = default)
    {
        List<int> matches = FindMatches(content, query, options);
        if (matches.Count == 0)
        {
            return -1;
        }

        foreach (int match in matches)
        {
            if (match >= fromOffset)
            {
                return match;
            }
        }
        return matches[0];
    }

    /// <summary>
    /// One "Replace" step (ADR 0058 §3). When the selection
    /// (<paramref name="selectionStart"/>, <paramref name="selectionLength"/>) is exactly an
    /// occurrence of <paramref name="query"/>, it is replaced by <paramref name="replacement"/>
    /// and the next occurrence after the inserted text is returned for selection. Otherwise
    /// nothing is replaced and the next occurrence from the selection start is returned, so a
    /// second click performs the replacement — the same two-step behavior as desktop editors.
    /// </summary>
    public static TextEditorReplaceResult Replace(
        string content,
        string query,
        string replacement,
        int selectionStart,
        int selectionLength,
        TextEditorFindOptions options = default)
    {
        content ??= string.Empty;
        replacement ??= string.Empty;
        if (string.IsNullOrEmpty(query))
        {
            return new TextEditorReplaceResult(content, false, -1);
        }

        List<int> matches = FindMatches(content, query, options);
        bool selectionIsMatch = selectionLength == query.Length && matches.Contains(selectionStart);
        if (!selectionIsMatch)
        {
            return new TextEditorReplaceResult(content, false, FindNextFrom(content, query, Math.Max(0, selectionStart), options));
        }

        string updated = string.Concat(
            content.AsSpan(0, selectionStart),
            replacement,
            content.AsSpan(selectionStart + selectionLength));

        // Search after the inserted text so a replacement that contains the query
        // ("cat" -> "cats") is not matched again in place.
        int next = FindNextFrom(updated, query, selectionStart + replacement.Length, options);
        return new TextEditorReplaceResult(updated, true, next);
    }

    /// <summary>
    /// Replaces every occurrence in a single pass and returns the new content plus the number
    /// of replacements, so the editor applies it as one document modification (ADR 0058 §3).
    /// </summary>
    public static TextEditorReplaceAllResult ReplaceAll(
        string content,
        string query,
        string replacement,
        TextEditorFindOptions options = default)
    {
        content ??= string.Empty;
        replacement ??= string.Empty;
        List<int> matches = FindMatches(content, query, options);
        if (matches.Count == 0)
        {
            return new TextEditorReplaceAllResult(content, 0);
        }

        StringBuilder builder = new(content.Length + (matches.Count * Math.Max(0, replacement.Length - query.Length)));
        int cursor = 0;
        foreach (int match in matches)
        {
            builder.Append(content, cursor, match - cursor).Append(replacement);
            cursor = match + query.Length;
        }
        builder.Append(content, cursor, content.Length - cursor);
        return new TextEditorReplaceAllResult(builder.ToString(), matches.Count);
    }

    /// <summary>Formats the Replace All summary shown in the panel ("1 replacement", "12 replacements").</summary>
    /// <param name="count">How many occurrences were replaced.</param>
    /// <param name="text">
    /// The editor's texts in the window's language; <see langword="null"/> reads them in the language of
    /// the process running the code (English outside any app).
    /// </param>
    public static string DescribeReplacementCount(int count, Microsoft.Extensions.Localization.IStringLocalizer? text = null) =>
        Strings.Plural(text ?? AppSdk.Globalization.KazoLocalization.GetLocalizer<Strings>(), "Replace.Count", count);

    private static bool IsWholeWordAt(string content, int start, int length)
    {
        int end = start + length;
        bool boundaryBefore = start == 0 || !IsWordChar(content[start - 1]) || !IsWordChar(content[start]);
        bool boundaryAfter = end == content.Length || !IsWordChar(content[end]) || !IsWordChar(content[end - 1]);
        return boundaryBefore && boundaryAfter;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
