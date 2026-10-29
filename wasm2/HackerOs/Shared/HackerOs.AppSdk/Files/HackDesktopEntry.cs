using System.Diagnostics.CodeAnalysis;
using System.Text;
using HackerOs.AppSdk.IO;

namespace HackerOs.AppSdk.Files;

/// <summary>
/// An app shortcut: a <c>.desktop</c> file in the freedesktop.org "Desktop Entry" format (ADR 0152 §5,
/// ADR 0160 §6), as the desktop, the file control and the Start menu write and read it.
/// </summary>
/// <remarks>
/// <code>
/// [Desktop Entry]
/// Type=Application
/// Name=Text Editor
/// Exec=org.hackeros.text-editor /home/alice/notes.txt
/// </code>
/// <para>
/// <c>Exec</c> starts with the app id, then its arguments, quoted as the specification says (an
/// argument with a space or a reserved character is put in double quotes, where <c>"</c>, <c>`</c>,
/// <c>$</c> and <c>\</c> take a backslash). Field codes (<c>%f</c>, <c>%U</c>…) are dropped: a shortcut
/// launches its app with its own arguments only. <c>Icon</c> is optional.
/// </para>
/// </remarks>
/// <param name="Name">The name shown under the icon.</param>
/// <param name="AppId">The app launched.</param>
/// <param name="Arguments">Its arguments, without the app id.</param>
/// <param name="Icon">An icon name or path; <see langword="null"/> shows the app's own icon.</param>
public sealed record HackDesktopEntry(string Name, string AppId, IReadOnlyList<string> Arguments, string? Icon = null)
{
    /// <summary>The file extension of a shortcut.</summary>
    public const string Extension = ".desktop";

    /// <summary>The only group the reader understands.</summary>
    public const string Group = "Desktop Entry";

    /// <summary>A shortcut larger than this is not read (it is not a shortcut).</summary>
    public const int MaxFileSize = 16 * 1024;

    private const string ReservedInQuotes = "\"`$\\";
    private const string NeedsQuotes = " \t\n\"'\\><~|&;$*?#()`";

    /// <summary>Gets whether <paramref name="path"/> names a shortcut (its extension is <c>.desktop</c>).</summary>
    /// <param name="path">A file name or path.</param>
    public static bool IsDesktopFile(string? path) =>
        path is not null && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) && path.Length > Extension.Length;

    /// <summary>
    /// Reads a shortcut. <see langword="false"/> for anything that is not one: no <c>[Desktop Entry]</c>
    /// group first, a <c>Type</c> other than <c>Application</c>, no <c>Name</c> or no <c>Exec</c>, or an
    /// <c>Exec</c> that does not parse. Such a file stays a text file (ADR 0152 §5).
    /// </summary>
    /// <param name="content">The file's text.</param>
    /// <param name="entry">The shortcut read.</param>
    public static bool TryParse(string? content, [NotNullWhen(true)] out HackDesktopEntry? entry)
    {
        entry = null;
        if (ReadGroup(content) is not { } values)
        {
            return false;
        }

        if (!values.TryGetValue("Type", out string? type) || type != "Application"
            || !values.TryGetValue("Name", out string? name) || UnescapeString(name) is not { Length: > 0 } unescapedName
            || !values.TryGetValue("Exec", out string? exec)
            || SplitExec(UnescapeString(exec)) is not { Count: > 0 } words
            || !IsAppId(words[0]))
        {
            return false;
        }

        string? icon = values.TryGetValue("Icon", out string? rawIcon) && UnescapeString(rawIcon) is { Length: > 0 } unescapedIcon
            ? unescapedIcon
            : null;
        entry = new HackDesktopEntry(unescapedName, words[0], [.. words.Skip(1)], icon);
        return true;
    }

    /// <summary>Writes this shortcut in the "Desktop Entry" format.</summary>
    public string Format()
    {
        StringBuilder text = new();
        text.Append('[').Append(Group).Append("]\n");
        text.Append("Type=Application\n");
        text.Append("Name=").Append(EscapeString(Name)).Append('\n');
        text.Append("Exec=").Append(EscapeString(JoinExec([AppId, .. Arguments]))).Append('\n');
        if (!string.IsNullOrEmpty(Icon))
        {
            text.Append("Icon=").Append(EscapeString(Icon)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Writes a shortcut to <paramref name="appId"/> in <paramref name="directory"/>, with the rights of
    /// the ambient app (ADR 0043): <c>&lt;name&gt;.desktop</c>, or <c>&lt;name&gt; (2).desktop</c> and so on
    /// when the name is taken.
    /// </summary>
    /// <param name="directory">The folder, an absolute path (<c>~/Desktop</c> resolved).</param>
    /// <param name="appId">The app launched.</param>
    /// <param name="name">The name shown, usually the app's name.</param>
    /// <param name="arguments">Its arguments; none by default.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>The path of the new file.</returns>
    /// <exception cref="ArgumentException"><paramref name="appId"/> is not an app id.</exception>
    public static async Task<string> CreateShortcutAsync(
        string directory,
        string appId,
        string name,
        IReadOnlyList<string>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        if (!IsAppId(appId))
        {
            throw new ArgumentException($"'{appId}' is not an app id.", nameof(appId));
        }

        HackDesktopEntry entry = new(string.IsNullOrWhiteSpace(name) ? appId : name.Trim(), appId, arguments ?? []);
        return await WriteUniqueAsync(directory, FileNameStem(entry.Name, appId), entry.Format(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the <c>[Desktop Entry]</c> group of a key file; <see langword="null"/> when the text is not
    /// one (empty, too large, another group first, a line that is neither a group, a comment nor a key).
    /// The first occurrence of a key wins, as in GLib's key file reader.
    /// </summary>
    internal static Dictionary<string, string>? ReadGroup(string? content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > MaxFileSize)
        {
            return null;
        }

        string? group = null;
        bool seenGroup = false;
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[')
            {
                if (line[^1] != ']')
                {
                    return null;
                }

                group = line[1..^1];
                // The specification: the "Desktop Entry" group comes first.
                if (!seenGroup && group != Group)
                {
                    return null;
                }

                seenGroup = true;
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0 || group is null)
            {
                return null;
            }

            if (group == Group)
            {
                values.TryAdd(line[..separator].Trim(), line[(separator + 1)..].Trim());
            }
        }

        return values;
    }

    /// <summary>
    /// Writes <paramref name="text"/> to <c>&lt;stem&gt;.desktop</c> in <paramref name="directory"/>, or to
    /// <c>&lt;stem&gt; (2).desktop</c> and so on when the name is taken; returns the path written.
    /// </summary>
    internal static async Task<string> WriteUniqueAsync(string directory, string stem, string text, CancellationToken cancellationToken)
    {
        string folder = directory.TrimEnd('/');
        for (int attempt = 1; ; attempt++)
        {
            string fileName = attempt == 1 ? stem + Extension : $"{stem} ({attempt}){Extension}";
            string path = folder.Length == 0 ? "/" + fileName : $"{folder}/{fileName}";
            if (await HackFile.ExistsAsync(path, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            await HackFile.WriteAllTextAsync(path, text, cancellationToken).ConfigureAwait(false);
            return path;
        }
    }

    /// <summary>Gets whether <paramref name="value"/> looks like an app id (<c>org.hackeros.text-editor</c>).</summary>
    /// <param name="value">The first word of <c>Exec</c>.</param>
    public static bool IsAppId(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= 255
        && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
        && value[0] != '.' && value[^1] != '.';

    /// <summary>Splits an <c>Exec</c> value (already string-unescaped) into words; <see langword="null"/> when a quote is not closed.</summary>
    /// <param name="exec">The value.</param>
    public static IReadOnlyList<string>? SplitExec(string exec)
    {
        ArgumentNullException.ThrowIfNull(exec);
        List<string> words = [];
        StringBuilder current = new();
        bool inWord = false;
        for (int index = 0; index < exec.Length; index++)
        {
            char c = exec[index];
            if (c == '"')
            {
                inWord = true;
                index++;
                for (; index < exec.Length && exec[index] != '"'; index++)
                {
                    if (exec[index] == '\\' && index + 1 < exec.Length && ReservedInQuotes.Contains(exec[index + 1]))
                    {
                        index++;
                    }

                    current.Append(exec[index]);
                }

                if (index >= exec.Length)
                {
                    return null;
                }
            }
            else if (c is ' ' or '\t')
            {
                Flush();
            }
            else if (c == '%' && index + 1 < exec.Length)
            {
                // Field codes: %% is a percent sign, any other code is dropped (deprecated or for files).
                index++;
                if (exec[index] == '%')
                {
                    current.Append('%');
                    inWord = true;
                }
            }
            else
            {
                current.Append(c);
                inWord = true;
            }
        }

        Flush();
        return words;

        void Flush()
        {
            if (inWord)
            {
                words.Add(current.ToString());
            }

            current.Clear();
            inWord = false;
        }
    }

    /// <summary>Joins words into an <c>Exec</c> value, quoting those that need it (before string escaping).</summary>
    /// <param name="words">The app id, then its arguments.</param>
    public static string JoinExec(IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        return string.Join(' ', words.Select(static word =>
        {
            string escaped = word.Replace("%", "%%", StringComparison.Ordinal);
            if (escaped.Length > 0 && !escaped.Any(NeedsQuotes.Contains))
            {
                return escaped;
            }

            StringBuilder quoted = new("\"");
            foreach (char c in escaped)
            {
                if (ReservedInQuotes.Contains(c))
                {
                    quoted.Append('\\');
                }

                quoted.Append(c);
            }

            return quoted.Append('"').ToString();
        }));
    }

    /// <summary>The escapes of a "string" value: <c>\s</c>, <c>\n</c>, <c>\t</c>, <c>\r</c>, <c>\\</c>.</summary>
    internal static string UnescapeString(string value)
    {
        if (!value.Contains('\\'))
        {
            return value;
        }

        StringBuilder text = new(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length)
            {
                text.Append(value[index]);
                continue;
            }

            index++;
            text.Append(value[index] switch
            {
                's' => ' ',
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                _ => value[index]
            });
        }

        return text.ToString();
    }

    internal static string EscapeString(string value)
    {
        StringBuilder text = new(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char c = value[index];
            text.Append(c switch
            {
                '\\' => "\\\\",
                '\n' => "\\n",
                '\t' => "\\t",
                '\r' => "\\r",
                // A leading space would be trimmed by every reader.
                ' ' when index == 0 => "\\s",
                _ => c.ToString()
            });
        }

        return text.ToString();
    }

    /// <summary>A file name from the shortcut's name: no slash, no control character, no leading dot.</summary>
    internal static string FileNameStem(string name, string fallback)
    {
        string cleaned = new([.. name.Select(static c => c == '/' || char.IsControl(c) ? '-' : c)]);
        cleaned = cleaned.Trim().TrimStart('.');
        if (cleaned.Length > 200)
        {
            cleaned = cleaned[..200];
        }

        return cleaned.Length == 0 ? fallback : cleaned;
    }
}
