using System.Text;

namespace Kazo.Os.Simulation.Abstractions.Settings;

/// <summary>
/// Describes one failure while parsing a Linux-like `.config` settings document.
/// </summary>
/// <param name="LineNumber">One-based source line number of the failure.</param>
/// <param name="Code">Stable parser error code.</param>
public sealed record ConfigDocumentParseError(int LineNumber, string Code);

/// <summary>
/// Contains the outcome of parsing a `.config` document.
/// </summary>
/// <param name="Values">Parsed key/value pairs keyed by their qualified `[Group]key` identity.</param>
/// <param name="Errors">Every parse failure in source order; empty when parsing succeeded.</param>
public sealed record ConfigDocumentParseResult(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<ConfigDocumentParseError> Errors)
{
    /// <summary>Gets whether the document parsed without any structural errors.</summary>
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// Parses and serializes the small Linux-like `.config` settings grammar defined by ADR 0011.
/// </summary>
/// <remarks>
/// Supports `#` full-line comments, optional `[GroupName]` sections, `key=value` pairs, and the
/// value escapes `\#`, `\=`, `\\`, `\n`, `\r`, and `\t`. Multiline values and inline trailing
/// comments are explicitly deferred by the ADR.
/// </remarks>
public static class ConfigDocumentFormat
{
    /// <summary>Parses complete `.config` document text.</summary>
    /// <param name="content">Complete UTF-8 document content.</param>
    /// <returns>Parsed values and any structural errors.</returns>
    public static ConfigDocumentParseResult Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        HashSet<string> seenSections = new(StringComparer.Ordinal);
        List<ConfigDocumentParseError> errors = [];
        string? currentGroup = null;
        int lineNumber = 0;

        foreach (string rawLine in content.Split('\n'))
        {
            lineNumber++;
            string line = rawLine.TrimEnd('\r');
            string trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            if (trimmed[0] == '[')
            {
                if (trimmed[^1] != ']' || trimmed.Length < 3)
                {
                    errors.Add(new ConfigDocumentParseError(lineNumber, "config.malformed-section"));
                    continue;
                }

                string group = trimmed[1..^1].Trim();
                if (group.Length == 0)
                {
                    errors.Add(new ConfigDocumentParseError(lineNumber, "config.malformed-section"));
                    continue;
                }

                if (!seenSections.Add(group))
                {
                    errors.Add(new ConfigDocumentParseError(lineNumber, "config.duplicate-section"));
                    continue;
                }

                currentGroup = group;
                continue;
            }

            int separatorIndex = FindUnescapedEquals(trimmed);
            if (separatorIndex < 0)
            {
                errors.Add(new ConfigDocumentParseError(lineNumber, "config.malformed-key-value"));
                continue;
            }

            string key = trimmed[..separatorIndex].Trim();
            string rawValue = trimmed[(separatorIndex + 1)..];
            if (key.Length == 0)
            {
                errors.Add(new ConfigDocumentParseError(lineNumber, "config.malformed-key-value"));
                continue;
            }

            string qualifiedKey = currentGroup is null ? key : $"{currentGroup}.{key}";
            if (!values.TryAdd(qualifiedKey, Unescape(rawValue)))
            {
                errors.Add(new ConfigDocumentParseError(lineNumber, "config.duplicate-key"));
            }
        }

        return new ConfigDocumentParseResult(values, errors);
    }

    /// <summary>
    /// Serializes declared schema fields into deterministic `.config` document text.
    /// </summary>
    /// <param name="schema">Schema declaring field order, grouping, and defaults.</param>
    /// <param name="values">Current values keyed by qualified `[Group]key` identity; missing keys use the field default.</param>
    /// <returns>Deterministic document text with root keys first, then one section per declared group.</returns>
    public static string Serialize(SettingsSchema schema, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(values);

        StringBuilder builder = new();
        builder.Append("schemaVersion=").Append(schema.SchemaVersion).Append('\n');

        IEnumerable<SettingFieldDeclaration> ordered = schema.Fields.Values
            .OrderBy(static field => field.Group is null ? 0 : 1)
            .ThenBy(static field => field.Group, StringComparer.Ordinal)
            .ThenBy(static field => field.Key, StringComparer.Ordinal);

        string? currentGroup = null;
        foreach (SettingFieldDeclaration field in ordered)
        {
            if (field.Group != currentGroup)
            {
                if (field.Group is not null)
                {
                    builder.Append('[').Append(field.Group).Append("]\n");
                }

                currentGroup = field.Group;
            }

            string value = values.TryGetValue(field.QualifiedKey, out string? provided)
                ? provided
                : field.DefaultValue;
            builder.Append(field.Key).Append('=').Append(Escape(value)).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Finds the first `=` not preceded by a `\` escape, or -1 when there is none.</summary>
    /// <param name="text">Trimmed key/value line.</param>
    internal static int FindUnescapedEquals(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\\')
            {
                index++;
                continue;
            }

            if (text[index] == '=')
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Decodes the value escapes of the grammar (`\#`, `\=`, `\\`, `\n`, `\r`, `\t`).</summary>
    /// <param name="value">Raw value text as written after `=`.</param>
    /// <returns>The decoded value.</returns>
    public static string UnescapeValue(string value) => Unescape(value);

    /// <summary>Encodes a value so it round-trips through <see cref="UnescapeValue"/>.</summary>
    /// <param name="value">Decoded value.</param>
    /// <returns>Escaped text safe to write after `=`.</returns>
    public static string EscapeValue(string value) => Escape(value);

    private static string Unescape(string value)
    {
        StringBuilder builder = new(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current == '\\' && index + 1 < value.Length)
            {
                index++;
                builder.Append(value[index] switch
                {
                    '#' => '#',
                    '=' => '=',
                    '\\' => '\\',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    var other => other
                });
                continue;
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    private static string Escape(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char current in value)
        {
            switch (current)
            {
                case '#':
                    builder.Append("\\#");
                    break;
                case '=':
                    builder.Append("\\=");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(current);
                    break;
            }
        }

        return builder.ToString();
    }
}
