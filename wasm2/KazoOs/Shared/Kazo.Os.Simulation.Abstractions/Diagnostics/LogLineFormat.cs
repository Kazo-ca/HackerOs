using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Kazo.Os.Simulation.Abstractions.Diagnostics;

/// <summary>
/// One log line read back from a <c>/var/log</c> file by <see cref="LogLineFormat.TryParse"/>
/// (ADR 0045, section 2).
/// </summary>
/// <param name="TimestampUtc">UTC simulation time written at the start of the line.</param>
/// <param name="Severity">Severity decoded from the five-character level column.</param>
/// <param name="Source">
/// Logical source: the <c>ILogger</c> category in <c>syslog</c>, the app ID in an app log, or the
/// audit action in <c>auth.log</c>.
/// </param>
/// <param name="ProcessId">PID written between brackets after the source, when a process was known.</param>
/// <param name="Message">Unescaped message text.</param>
/// <param name="Properties">Structured properties, already redacted when written, sorted by key.</param>
public sealed record LogLine(
    DateTimeOffset TimestampUtc,
    DiagnosticSeverity Severity,
    string Source,
    long? ProcessId,
    string Message,
    IReadOnlyDictionary<string, string> Properties);

/// <summary>
/// Formats and parses the single-line, syslog-inspired text format shared by every file under
/// <c>/var/log</c> (ADR 0045, section 2):
/// <c>&lt;timestamp&gt; &lt;LEVEL&gt; &lt;source&gt;[&lt;pid&gt;]: &lt;message&gt;[ {key=value, ...}]</c>.
/// </summary>
/// <remarks>
/// <para>
/// One entry always occupies exactly one line, so <c>grep</c>, <c>tail</c> and <c>wc -l</c> work on
/// entries rather than fragments. To guarantee that, the message escapes backslash, line feed,
/// carriage return and tab as <c>\\</c>, <c>\n</c>, <c>\r</c> and <c>\t</c>. It also escapes
/// <c>{</c> as <c>\{</c>: an unescaped <c>" {"</c> is what marks the start of the property block, so a
/// message can never be mistaken for properties when read back.
/// </para>
/// <para>
/// Inside the property block, keys and values additionally escape <c>,</c>, <c>=</c>, <c>{</c> and
/// <c>}</c>, the block's own delimiters.
/// </para>
/// <para>
/// Lines returned by the <c>Format</c> methods carry no line terminator; writers append
/// <see cref="LineTerminator"/>.
/// </para>
/// </remarks>
public static class LogLineFormat
{
    /// <summary>The terminator every log line ends with on disk.</summary>
    public const char LineTerminator = '\n';

    /// <summary>Width of the level column; shorter level names are padded with spaces.</summary>
    public const int LevelWidth = 5;

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Returns the level column text for <paramref name="severity"/>, without padding.</summary>
    /// <param name="severity">Severity to translate.</param>
    /// <returns>One of <c>TRACE</c>, <c>DEBUG</c>, <c>INFO</c>, <c>WARN</c>, <c>ERROR</c>, <c>CRIT</c>.</returns>
    public static string LevelName(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Trace => "TRACE",
        DiagnosticSeverity.Debug => "DEBUG",
        DiagnosticSeverity.Information => "INFO",
        DiagnosticSeverity.Warning => "WARN",
        DiagnosticSeverity.Error => "ERROR",
        DiagnosticSeverity.Critical => "CRIT",
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unknown severity.")
    };

    /// <summary>Formats one log line from its parts.</summary>
    /// <param name="timestampUtc">Simulation time of the entry; converted to UTC.</param>
    /// <param name="severity">Severity of the entry.</param>
    /// <param name="source">Logical source; whitespace is replaced by <c>_</c> so the column stays one token.</param>
    /// <param name="processId">PID to write after the source, or <see langword="null"/> when no process is known.</param>
    /// <param name="message">Message text; escaped so the entry stays on one line.</param>
    /// <param name="properties">Optional properties, expected to be already redacted; written sorted by key.</param>
    /// <returns>The formatted line, without line terminator.</returns>
    public static string Format(
        DateTimeOffset timestampUtc,
        DiagnosticSeverity severity,
        string source,
        long? processId,
        string message,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        StringBuilder builder = new(64 + message.Length);
        builder.Append(timestampUtc.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture));
        builder.Append(' ');
        builder.Append(LevelName(severity).PadRight(LevelWidth));
        builder.Append(' ');
        builder.Append(SanitizeSource(source));
        if (processId is long pid)
        {
            builder.Append('[').Append(pid.ToString(CultureInfo.InvariantCulture)).Append(']');
        }

        builder.Append(": ");
        AppendEscaped(builder, message, escapePropertyDelimiters: false);
        AppendProperties(builder, properties);
        return builder.ToString();
    }

    /// <summary>Formats one diagnostic entry, using its category as source.</summary>
    /// <param name="entry">Entry to format; its properties are expected to be already redacted.</param>
    /// <param name="processId">PID to write after the source, when known.</param>
    /// <returns>The formatted line, without line terminator.</returns>
    public static string Format(DiagnosticEntry entry, long? processId = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Format(entry.TimestampUtc, entry.Severity, entry.Category, processId, entry.Message, entry.Properties);
    }

    /// <summary>
    /// Formats one audit entry for <c>auth.log</c>:
    /// <c>&lt;timestamp&gt; &lt;LEVEL&gt; &lt;action&gt;: actor=&lt;Actor&gt; subject=&lt;Subject&gt; outcome=&lt;Outcome&gt; {properties}</c>,
    /// at <c>INFO</c> for <see cref="AuditOutcome.Success"/> and <c>WARN</c> otherwise.
    /// </summary>
    /// <param name="entry">Audit entry to format; its properties are expected to be already redacted.</param>
    /// <returns>The formatted line, without line terminator.</returns>
    public static string FormatAudit(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        DiagnosticSeverity severity = entry.Outcome == AuditOutcome.Success
            ? DiagnosticSeverity.Information
            : DiagnosticSeverity.Warning;
        string message = $"actor={entry.Actor} subject={entry.Subject} outcome={entry.Outcome}";
        return Format(entry.TimestampUtc, severity, entry.Action, null, message, entry.Properties);
    }

    /// <summary>Parses one line written by <see cref="Format(DateTimeOffset, DiagnosticSeverity, string, long?, string, IReadOnlyDictionary{string, string}?)"/>.</summary>
    /// <param name="line">Line to parse; a trailing <c>\n</c> or <c>\r\n</c> is ignored.</param>
    /// <param name="entry">The parsed line, when parsing succeeded.</param>
    /// <returns><see langword="false"/> for a blank, truncated, or foreign line; never throws.</returns>
    public static bool TryParse(string? line, [NotNullWhen(true)] out LogLine? entry)
    {
        entry = null;
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        ReadOnlySpan<char> text = line.AsSpan().TrimEnd("\r\n");

        // Timestamp column.
        int timestampEnd = text.IndexOf(' ');
        if (timestampEnd <= 0
            || !DateTimeOffset.TryParseExact(
                text[..timestampEnd],
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset timestamp))
        {
            return false;
        }

        // Fixed-width level column, then one separating space.
        int levelStart = timestampEnd + 1;
        if (text.Length < levelStart + LevelWidth + 1 || text[levelStart + LevelWidth] != ' ')
        {
            return false;
        }

        if (!TryParseLevel(text.Slice(levelStart, LevelWidth).TrimEnd(' '), out DiagnosticSeverity severity))
        {
            return false;
        }

        // "<source>[<pid>]: " header. Sources never contain whitespace (see SanitizeSource), so the
        // first ": " always ends the header.
        ReadOnlySpan<char> rest = text[(levelStart + LevelWidth + 1)..];
        int headerEnd = rest.IndexOf(": ", StringComparison.Ordinal);
        if (headerEnd <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> header = rest[..headerEnd];
        long? processId = null;
        string source = header.ToString();
        int bracket = header.LastIndexOf('[');
        if (bracket > 0 && header[^1] == ']'
            && long.TryParse(header[(bracket + 1)..^1], NumberStyles.None, CultureInfo.InvariantCulture, out long pid))
        {
            processId = pid;
            source = header[..bracket].ToString();
        }

        ReadOnlySpan<char> body = rest[(headerEnd + 2)..];
        int propertiesStart = FindPropertiesStart(body);
        string message;
        IReadOnlyDictionary<string, string> properties;
        if (propertiesStart < 0)
        {
            message = Unescape(body);
            properties = new SortedDictionary<string, string>(StringComparer.Ordinal);
        }
        else
        {
            message = Unescape(body[..propertiesStart]);
            if (!TryParseProperties(body[(propertiesStart + 2)..^1], out properties))
            {
                return false;
            }
        }

        entry = new LogLine(timestamp, severity, source, processId, message, properties);
        return true;
    }

    /// <summary>Replaces whitespace in a source with <c>_</c>, so the header stays one token.</summary>
    private static string SanitizeSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "unknown";
        }

        StringBuilder? builder = null;
        for (int index = 0; index < source.Length; index++)
        {
            if (char.IsWhiteSpace(source[index]))
            {
                builder ??= new StringBuilder(source);
                builder[index] = '_';
            }
        }

        return builder?.ToString() ?? source;
    }

    private static void AppendProperties(StringBuilder builder, IReadOnlyDictionary<string, string>? properties)
    {
        if (properties is null || properties.Count == 0)
        {
            return;
        }

        builder.Append(" {");
        bool first = true;
        foreach (KeyValuePair<string, string> pair in properties.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(", ");
            }

            first = false;
            AppendEscaped(builder, pair.Key, escapePropertyDelimiters: true);
            builder.Append('=');
            AppendEscaped(builder, pair.Value ?? string.Empty, escapePropertyDelimiters: true);
        }

        builder.Append('}');
    }

    private static void AppendEscaped(StringBuilder builder, string value, bool escapePropertyDelimiters)
    {
        foreach (char character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                case '\t':
                    builder.Append(@"\t");
                    break;
                case '{':
                    builder.Append(@"\{");
                    break;
                case '}' or ',' or '=' when escapePropertyDelimiters:
                    builder.Append('\\').Append(character);
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }
    }

    /// <summary>
    /// Returns the index of the unescaped <c>" {"</c> that opens the trailing property block, or -1.
    /// The block must run to the end of the line and close with <c>}</c>.
    /// </summary>
    private static int FindPropertiesStart(ReadOnlySpan<char> body)
    {
        for (int index = 0; index < body.Length; index++)
        {
            char character = body[index];
            if (character == '\\')
            {
                index++;
                continue;
            }

            if (character == '{' && index > 0 && body[index - 1] == ' ' && body[^1] == '}')
            {
                return index - 1;
            }
        }

        return -1;
    }

    private static bool TryParseProperties(ReadOnlySpan<char> block, out IReadOnlyDictionary<string, string> properties)
    {
        SortedDictionary<string, string> parsed = new(StringComparer.Ordinal);
        properties = parsed;

        StringBuilder key = new();
        StringBuilder value = new();
        bool inValue = false;
        for (int index = 0; index < block.Length; index++)
        {
            char character = block[index];
            if (character == '\\' && index + 1 < block.Length)
            {
                (inValue ? value : key).Append(UnescapeCharacter(block[++index]));
                continue;
            }

            if (character == '=' && !inValue)
            {
                inValue = true;
                continue;
            }

            if (character == ',' && index + 1 < block.Length && block[index + 1] == ' ')
            {
                if (!inValue || key.Length == 0)
                {
                    return false;
                }

                parsed[key.ToString()] = value.ToString();
                key.Clear();
                value.Clear();
                inValue = false;
                index++;
                continue;
            }

            (inValue ? value : key).Append(character);
        }

        if (!inValue || key.Length == 0)
        {
            return false;
        }

        parsed[key.ToString()] = value.ToString();
        return true;
    }

    private static string Unescape(ReadOnlySpan<char> value)
    {
        if (!value.Contains('\\'))
        {
            return value.ToString();
        }

        StringBuilder builder = new(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (character == '\\' && index + 1 < value.Length)
            {
                builder.Append(UnescapeCharacter(value[++index]));
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static char UnescapeCharacter(char escaped) => escaped switch
    {
        'n' => '\n',
        'r' => '\r',
        't' => '\t',
        _ => escaped
    };

    private static bool TryParseLevel(ReadOnlySpan<char> level, out DiagnosticSeverity severity)
    {
        switch (level)
        {
            case "TRACE":
                severity = DiagnosticSeverity.Trace;
                return true;
            case "DEBUG":
                severity = DiagnosticSeverity.Debug;
                return true;
            case "INFO":
                severity = DiagnosticSeverity.Information;
                return true;
            case "WARN":
                severity = DiagnosticSeverity.Warning;
                return true;
            case "ERROR":
                severity = DiagnosticSeverity.Error;
                return true;
            case "CRIT":
                severity = DiagnosticSeverity.Critical;
                return true;
            default:
                severity = default;
                return false;
        }
    }
}
