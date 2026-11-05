namespace Kazo.Os.Simulation.Abstractions.Settings;

/// <summary>Identifies what one physical line of a `.config` document is.</summary>
public enum ConfigLineKind
{
    /// <summary>An empty or whitespace-only line.</summary>
    Blank = 1,

    /// <summary>A full-line `#` comment.</summary>
    Comment = 2,

    /// <summary>A `[GroupName]` section header.</summary>
    Section = 3,

    /// <summary>A `key=value` pair.</summary>
    KeyValue = 4,

    /// <summary>A line that matches no rule of the grammar (ignored by readers).</summary>
    Malformed = 5
}

/// <summary>
/// One physical line of a `.config` document, as seen by the tolerant line reader.
/// </summary>
/// <param name="LineNumber">One-based line number in the source document.</param>
/// <param name="Kind">What the line is.</param>
/// <param name="Raw">Exact source text of the line, without its trailing <c>\n</c>.</param>
/// <param name="Group">
/// For <see cref="ConfigLineKind.Section"/>, the section name; for <see cref="ConfigLineKind.KeyValue"/>,
/// the section the key belongs to (<see langword="null"/> for the document root).
/// </param>
/// <param name="Key">Unqualified key of a <see cref="ConfigLineKind.KeyValue"/> line.</param>
/// <param name="Value">Unescaped value of a <see cref="ConfigLineKind.KeyValue"/> line.</param>
/// <param name="InMalformedSection">
/// <see langword="true"/> for a key/value line that follows a malformed section header, whose
/// group therefore cannot be known; readers ignore such lines.
/// </param>
public sealed record ConfigLine(
    int LineNumber,
    ConfigLineKind Kind,
    string Raw,
    string? Group = null,
    string? Key = null,
    string? Value = null,
    bool InMalformedSection = false)
{
    /// <summary>Gets the `Group.key` (or root `key`) identity of a key/value line.</summary>
    public string? QualifiedKey => Kind != ConfigLineKind.KeyValue
        ? null
        : Group is null ? Key : $"{Group}.{Key}";
}

/// <summary>
/// Tolerant, line-preserving reader for the `.config` grammar of ADR 0011, used by the layered
/// settings engine of ADR 0044.
/// </summary>
/// <remarks>
/// Unlike <see cref="ConfigDocumentFormat.Parse"/>, which rejects a whole document on the first
/// structural problem, this reader never fails: it classifies every physical line so callers can
/// ignore bad lines one by one (ADR 0044 section 5) and rewrite a single line without touching
/// comments, ordering, or keys they do not understand (<see cref="ConfigDocumentEditor"/>).
/// A section name that appears twice simply continues that section, like most INI readers.
/// </remarks>
public static class ConfigDocumentLines
{
    /// <summary>Splits and classifies every line of <paramref name="content"/>.</summary>
    /// <param name="content">Complete document text; <c>\r\n</c> line endings are tolerated.</param>
    /// <returns>One entry per physical line, in source order.</returns>
    public static IReadOnlyList<ConfigLine> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        List<ConfigLine> lines = [];
        string? currentGroup = null;
        bool inMalformedSection = false;
        string[] rawLines = content.Split('\n');

        // A trailing newline produces one empty final element that is not a real line.
        int count = rawLines.Length > 0 && rawLines[^1].Length == 0 ? rawLines.Length - 1 : rawLines.Length;
        for (int index = 0; index < count; index++)
        {
            int lineNumber = index + 1;
            string raw = rawLines[index];
            string trimmed = raw.TrimEnd('\r').Trim();

            if (trimmed.Length == 0)
            {
                lines.Add(new ConfigLine(lineNumber, ConfigLineKind.Blank, raw));
                continue;
            }

            if (trimmed[0] == '#')
            {
                lines.Add(new ConfigLine(lineNumber, ConfigLineKind.Comment, raw));
                continue;
            }

            if (trimmed[0] == '[')
            {
                string group = trimmed.Length >= 3 && trimmed[^1] == ']' ? trimmed[1..^1].Trim() : string.Empty;
                if (group.Length == 0)
                {
                    lines.Add(new ConfigLine(lineNumber, ConfigLineKind.Malformed, raw));
                    inMalformedSection = true;
                    continue;
                }

                currentGroup = group;
                inMalformedSection = false;
                lines.Add(new ConfigLine(lineNumber, ConfigLineKind.Section, raw, Group: group));
                continue;
            }

            int separator = ConfigDocumentFormat.FindUnescapedEquals(trimmed);
            string key = separator < 0 ? string.Empty : trimmed[..separator].Trim();
            if (key.Length == 0)
            {
                lines.Add(new ConfigLine(lineNumber, ConfigLineKind.Malformed, raw));
                continue;
            }

            lines.Add(new ConfigLine(
                lineNumber,
                ConfigLineKind.KeyValue,
                raw,
                currentGroup,
                key,
                ConfigDocumentFormat.UnescapeValue(trimmed[(separator + 1)..]),
                inMalformedSection));
        }

        return lines;
    }
}

/// <summary>
/// Rewrites single values of a `.config` document while preserving comments, order, unknown keys
/// and every untouched line exactly (ADR 0044 section 7).
/// </summary>
public static class ConfigDocumentEditor
{
    /// <summary>
    /// Sets <paramref name="key"/> in <paramref name="group"/> to <paramref name="value"/>.
    /// </summary>
    /// <param name="content">Current document text, or <see langword="null"/>/empty for a new file.</param>
    /// <param name="group">Section name, or <see langword="null"/> for the document root.</param>
    /// <param name="key">Unqualified key.</param>
    /// <param name="value">Unescaped value; escaping is applied here.</param>
    /// <returns>
    /// The new document text. The last existing occurrence of the key (the one readers honor) is
    /// replaced in place; otherwise the line is appended to the end of its section, creating the
    /// section at the end of the document when it does not exist yet.
    /// </returns>
    public static string SetValue(string? content, string? group, string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        string newLine = $"{key}={ConfigDocumentFormat.EscapeValue(value)}";
        List<string> raw = SplitRaw(content);
        IReadOnlyList<ConfigLine> lines = ConfigDocumentLines.Parse(content ?? string.Empty);

        ConfigLine? existing = lines.LastOrDefault(line =>
            line.Kind == ConfigLineKind.KeyValue
            && !line.InMalformedSection
            && line.Group == group
            && line.Key == key);
        if (existing is not null)
        {
            raw[existing.LineNumber - 1] = newLine;
            return JoinRaw(raw);
        }

        int insertAt = FindInsertionIndex(lines, group);
        if (insertAt >= 0)
        {
            raw.Insert(insertAt, newLine);
            return JoinRaw(raw);
        }

        // The section does not exist yet: append it, separated from previous content by a blank line.
        if (raw.Count > 0 && raw[^1].Trim().Length != 0)
        {
            raw.Add(string.Empty);
        }

        raw.Add($"[{group}]");
        raw.Add(newLine);
        return JoinRaw(raw);
    }

    /// <summary>Removes every occurrence of <paramref name="key"/> in <paramref name="group"/>.</summary>
    /// <param name="content">Current document text.</param>
    /// <param name="group">Section name, or <see langword="null"/> for the document root.</param>
    /// <param name="key">Unqualified key.</param>
    /// <returns>The new document text; unchanged when the key was absent.</returns>
    public static string RemoveValue(string? content, string? group, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        List<string> raw = SplitRaw(content);
        IReadOnlyList<ConfigLine> lines = ConfigDocumentLines.Parse(content ?? string.Empty);
        foreach (ConfigLine line in lines.Reverse())
        {
            if (line.Kind == ConfigLineKind.KeyValue && line.Group == group && line.Key == key)
            {
                raw.RemoveAt(line.LineNumber - 1);
            }
        }

        return JoinRaw(raw);
    }

    private static int FindInsertionIndex(IReadOnlyList<ConfigLine> lines, string? group)
    {
        if (group is null)
        {
            // Root keys go after the last root key, or before the first section header.
            int lastRootKey = -1;
            int firstSection = -1;
            foreach (ConfigLine line in lines)
            {
                if (IsSectionLike(line))
                {
                    firstSection = line.LineNumber - 1;
                    break;
                }

                if (line.Kind == ConfigLineKind.KeyValue)
                {
                    lastRootKey = line.LineNumber - 1;
                }
            }

            if (lastRootKey >= 0)
            {
                return lastRootKey + 1;
            }

            if (firstSection < 0)
            {
                return lines.Count;
            }

            // Keep a blank separator line between the new root key and the first section.
            int index = firstSection;
            while (index > 0 && lines[index - 1].Kind == ConfigLineKind.Blank)
            {
                index--;
            }

            return index;
        }

        // Grouped keys go after the last non-blank line of the last header of that section.
        int sectionHeader = -1;
        for (int index = lines.Count - 1; index >= 0; index--)
        {
            if (lines[index].Kind == ConfigLineKind.Section && lines[index].Group == group)
            {
                sectionHeader = index;
                break;
            }
        }

        if (sectionHeader < 0)
        {
            return -1;
        }

        int last = sectionHeader;
        for (int index = sectionHeader + 1; index < lines.Count; index++)
        {
            ConfigLine line = lines[index];
            if (IsSectionLike(line))
            {
                break;
            }

            if (line.Kind != ConfigLineKind.Blank)
            {
                last = index;
            }
        }

        return last + 1;
    }

    // A malformed "[..." header still ends the previous section for insertion purposes.
    private static bool IsSectionLike(ConfigLine line) =>
        line.Kind == ConfigLineKind.Section
        || (line.Kind == ConfigLineKind.Malformed && line.Raw.TrimStart().StartsWith('['));

    private static List<string> SplitRaw(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        List<string> raw = [.. content.Split('\n')];
        if (raw[^1].Length == 0)
        {
            raw.RemoveAt(raw.Count - 1);
        }

        return raw;
    }

    private static string JoinRaw(List<string> raw) =>
        raw.Count == 0 ? string.Empty : string.Join('\n', raw) + "\n";
}
