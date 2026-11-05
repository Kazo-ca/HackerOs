using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.ContextMenus;

/// <summary>
/// The entries a user hid from the file menus (ADR 0151 §6), in
/// <c>~/.config/kazoos/context-menus.config</c>:
/// </summary>
/// <remarks>
/// <code>
/// [hidden]
/// org.kazoos.file-explorer.extract-here=true
/// </code>
/// <para>
/// One key per hidden entry, <c>&lt;appId&gt;.&lt;name&gt;</c>. Keys contain dots, so the file is read
/// line by line with the tolerant reader of the <c>.config</c> files (ADR 0044 §5) and edited without
/// touching the other lines (ADR 0044 §7). Hiding a parent hides its submenu. The file is read at each
/// menu opening, so an edit made with <c>nano</c> applies to the next menu.
/// </para>
/// </remarks>
public sealed class FileContextMenuPreferences
{
    /// <summary>The file name under <c>~/.config/kazoos</c>.</summary>
    public const string FileName = "context-menus.config";

    /// <summary>The section of the hidden entries.</summary>
    public const string HiddenGroup = "hidden";

    /// <summary>The comment a new file starts with.</summary>
    public const string Header =
        "# ~/.config/kazoos/context-menus.config\n"
        + "# File menu entries added by apps that you hid (ADR 0151). One '<app id>.<entry>=true' line\n"
        + "# per hidden entry; change it in Settings > File menus.\n";

    private readonly IOsSettingsService _settings;

    /// <summary>Creates the preferences over the OS settings files of the session user.</summary>
    /// <param name="settings">OS settings files.</param>
    public FileContextMenuPreferences(IOsSettingsService settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Gets the file in the session user's configuration.</summary>
    public static OsSettingsFile File { get; } = new(OsSettingsLocation.UserConfig, FileName);

    /// <summary>Gets the key of one entry, <c>&lt;appId&gt;.&lt;name&gt;</c>.</summary>
    /// <param name="appId">The app.</param>
    /// <param name="name">The entry's name.</param>
    public static string KeyOf(string appId, string name) => $"{appId}.{name}";

    /// <summary>Reads the hidden entries of the session user; empty before login or without the file.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<IReadOnlySet<string>> GetHiddenAsync(CancellationToken cancellationToken = default) =>
        Parse(await _settings.ReadFileAsync(File, cancellationToken).ConfigureAwait(false));

    /// <summary>Hides or shows one entry for the session user.</summary>
    /// <param name="appId">The app.</param>
    /// <param name="name">The entry's name.</param>
    /// <param name="hidden">Whether to hide it.</param>
    /// <param name="cancellationToken">Cancels the read and the write.</param>
    /// <exception cref="InvalidOperationException">No user is logged in.</exception>
    public async ValueTask SetHiddenAsync(string appId, string name, bool hidden, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string? content = await _settings.ReadFileAsync(File, cancellationToken).ConfigureAwait(false);
        string? edited = Edit(content, KeyOf(appId, name), hidden);
        if (edited is not null && !string.Equals(edited, content, StringComparison.Ordinal))
        {
            await _settings.WriteFileAsync(File, edited, writer: null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the hidden keys of <paramref name="content"/>: a <c>true</c> value hides, anything else does not.</summary>
    /// <param name="content">The file's text, or <see langword="null"/>.</param>
    public static IReadOnlySet<string> Parse(string? content)
    {
        HashSet<string> hidden = new(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(content))
        {
            return hidden;
        }

        foreach (ConfigLine line in ConfigDocumentLines.Parse(content))
        {
            if (line.Kind == ConfigLineKind.KeyValue
                && !line.InMalformedSection
                && string.Equals(line.Group, HiddenGroup, StringComparison.Ordinal)
                && line.Key is { Length: > 0 } key)
            {
                if (string.Equals(line.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                {
                    hidden.Add(key);
                }
                else
                {
                    // The last line of a key wins, as in any .config document.
                    hidden.Remove(key);
                }
            }
        }

        return hidden;
    }

    /// <summary>Writes or removes the line of one entry, keeping every other line.</summary>
    /// <param name="content">The current text, or <see langword="null"/>.</param>
    /// <param name="key">The entry's key.</param>
    /// <param name="hidden">Whether it is hidden.</param>
    /// <returns>The new text; <see langword="null"/> when showing an entry finds no file.</returns>
    public static string? Edit(string? content, string key, bool hidden)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!hidden)
        {
            return content is null ? null : ConfigDocumentEditor.RemoveValue(content, HiddenGroup, key);
        }

        return ConfigDocumentEditor.SetValue(string.IsNullOrEmpty(content) ? Header : content, HiddenGroup, key, "true");
    }
}
