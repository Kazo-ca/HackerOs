using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace HackerOs.AppSdk.Files;

/// <summary>
/// A shortcut to a file or a folder: a <c>.desktop</c> file of type <c>Link</c> in the freedesktop.org
/// "Desktop Entry" format (ADR 0185 §13), as "Send to ▸ Desktop" writes it and the file control opens it.
/// </summary>
/// <remarks>
/// <code>
/// [Desktop Entry]
/// Type=Link
/// Name=report.txt
/// URL=file:///home/alice/Documents/report.txt
/// </code>
/// <para>
/// <c>URL</c> is a <c>file://</c> URL of an absolute path, each segment percent-encoded; no other scheme
/// is read. Opening the shortcut opens its target (a folder is entered, a file opens with its app); the
/// shortcut does not follow a target that moved. <c>Icon</c> is optional. An app shortcut is a
/// <see cref="HackDesktopEntry"/> (<c>Type=Application</c>).
/// </para>
/// </remarks>
/// <param name="Name">The name shown under the icon.</param>
/// <param name="Target">The absolute path of the file or folder it opens.</param>
/// <param name="Icon">An icon name or path; <see langword="null"/> shows the target's kind.</param>
public sealed record HackDesktopLink(string Name, string Target, string? Icon = null)
{
    private const string FileScheme = "file://";

    /// <summary>
    /// Reads a link shortcut. <see langword="false"/> for anything that is not one: no
    /// <c>[Desktop Entry]</c> group first, a <c>Type</c> other than <c>Link</c>, no <c>Name</c>, or a
    /// <c>URL</c> that is not a <c>file://</c> URL of an absolute path.
    /// </summary>
    /// <param name="content">The file's text.</param>
    /// <param name="link">The shortcut read.</param>
    public static bool TryParse(string? content, [NotNullWhen(true)] out HackDesktopLink? link)
    {
        link = null;
        if (HackDesktopEntry.ReadGroup(content) is not { } values
            || !values.TryGetValue("Type", out string? type) || type != "Link"
            || !values.TryGetValue("Name", out string? name) || HackDesktopEntry.UnescapeString(name) is not { Length: > 0 } unescapedName
            || !values.TryGetValue("URL", out string? url)
            || PathFromUrl(HackDesktopEntry.UnescapeString(url)) is not { } target)
        {
            return false;
        }

        string? icon = values.TryGetValue("Icon", out string? rawIcon) && HackDesktopEntry.UnescapeString(rawIcon) is { Length: > 0 } unescapedIcon
            ? unescapedIcon
            : null;
        link = new HackDesktopLink(unescapedName, target, icon);
        return true;
    }

    /// <summary>Writes this shortcut in the "Desktop Entry" format.</summary>
    /// <exception cref="ArgumentException"><see cref="Target"/> is not an absolute path.</exception>
    public string Format()
    {
        StringBuilder text = new();
        text.Append('[').Append(HackDesktopEntry.Group).Append("]\n");
        text.Append("Type=Link\n");
        text.Append("Name=").Append(HackDesktopEntry.EscapeString(Name)).Append('\n');
        text.Append("URL=").Append(HackDesktopEntry.EscapeString(ToUrl(Target))).Append('\n');
        if (!string.IsNullOrEmpty(Icon))
        {
            text.Append("Icon=").Append(HackDesktopEntry.EscapeString(Icon)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Writes a shortcut to <paramref name="target"/> in <paramref name="directory"/>, with the rights of
    /// the ambient app (ADR 0043): <c>&lt;name&gt;.desktop</c>, or <c>&lt;name&gt; (2).desktop</c> and so on
    /// when the name is taken. The target is not checked: a shortcut may outlive it.
    /// </summary>
    /// <param name="directory">The folder, an absolute path (<c>~/Desktop</c> resolved).</param>
    /// <param name="target">The absolute path of the file or folder.</param>
    /// <param name="name">The name shown; the target's own name by default.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>The path of the new file.</returns>
    /// <exception cref="ArgumentException"><paramref name="target"/> is not an absolute path.</exception>
    public static async Task<string> CreateLinkAsync(
        string directory,
        string target,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (target[0] != '/')
        {
            throw new ArgumentException($"'{target}' is not an absolute path.", nameof(target));
        }

        string trimmed = target.Length > 1 ? target.TrimEnd('/') : target;
        string ownName = trimmed.Length <= 1 ? "/" : trimmed[(trimmed.LastIndexOf('/') + 1)..];
        HackDesktopLink link = new(string.IsNullOrWhiteSpace(name) ? ownName : name.Trim(), trimmed.Length == 0 ? "/" : trimmed);
        string stem = HackDesktopEntry.FileNameStem(link.Name, "link");
        return await HackDesktopEntry.WriteUniqueAsync(directory, stem, link.Format(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The <c>file://</c> URL of an absolute path: <c>/a b/c</c> is <c>file:///a%20b/c</c>.</summary>
    /// <param name="path">An absolute path.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not an absolute path.</exception>
    public static string ToUrl(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path[0] != '/')
        {
            throw new ArgumentException($"'{path}' is not an absolute path.", nameof(path));
        }

        return FileScheme + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
    }

    /// <summary>The absolute path of a <c>file://</c> URL (an empty host or <c>localhost</c>); <see langword="null"/> for anything else.</summary>
    /// <param name="url">The URL.</param>
    public static string? PathFromUrl(string? url)
    {
        if (url is null || !url.StartsWith(FileScheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string rest = url[FileScheme.Length..];
        if (rest.StartsWith("localhost/", StringComparison.OrdinalIgnoreCase))
        {
            rest = rest["localhost".Length..];
        }

        if (rest.Length == 0 || rest[0] != '/' || rest.Contains('?') || rest.Contains('#'))
        {
            return null;
        }

        string path = Uri.UnescapeDataString(rest);
        return path.Contains('\0') ? null : path;
    }
}
