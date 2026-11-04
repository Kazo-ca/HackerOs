using System.Globalization;
using System.Text;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.AppSdk.Files;

/// <summary>What <c>~/.config/kazoos/desktop.config</c> holds (ADR 0160 §4).</summary>
/// <param name="Options">The <c>[view]</c> group.</param>
/// <param name="Positions">The <c>[positions]</c> group: the cell of each icon, by file name or system key.</param>
public sealed record KazoDesktopSettingsDocument(KazoDesktopOptions Options, IReadOnlyDictionary<string, KazoDesktopCell> Positions);

/// <summary>
/// Reads and writes the desktop's settings file, <c>~/.config/kazoos/desktop.config</c> (ADR 0152 §3,
/// ADR 0160 §4), like a desktop's <c>~/.config/</c> file: a plain <c>.config</c> document the user may
/// edit by hand, where the desktop app writes the positions and Settings ▸ Desktop the view.
/// </summary>
/// <remarks>
/// <code>
/// [view]
/// icon-size=medium
/// auto-arrange=false
/// align-to-grid=true
/// sort=name
/// show-home=true
/// show-trash=true
///
/// [positions]
/// ::home=0,0
/// ::trash=0,1
/// notes.txt=1,0
/// </code>
/// <para>
/// A position key is the file name, with <c>%</c>, <c>=</c>, <c>\</c>, <c>#</c>, <c>;</c>, <c>[</c>,
/// <c>]</c>, control characters and leading or trailing spaces written <c>%XX</c> (UTF-8), so any
/// name survives the <c>.config</c> grammar. A bad line or value is ignored: the default applies.
/// Writes keep comments and unknown keys of <c>[view]</c>; the <c>[positions]</c> group is rewritten
/// whole.
/// </para>
/// </remarks>
public static class KazoDesktopSettings
{
    /// <summary>The group of the display options.</summary>
    public const string ViewGroup = "view";

    /// <summary>The group of the icon positions.</summary>
    public const string PositionsGroup = "positions";

    private const string ReservedInKeys = "%=\\#;[]";

    /// <summary>Gets the settings file of a user.</summary>
    /// <param name="homeDirectory">The user's home directory.</param>
    public static string GetPath(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        return homeDirectory.TrimEnd('/') + "/.config/kazoos/desktop.config";
    }

    /// <summary>Reads a document's text; anything missing or malformed takes its default.</summary>
    /// <param name="content">The file's text, or <see langword="null"/> when there is no file.</param>
    public static KazoDesktopSettingsDocument Parse(string? content)
    {
        KazoDesktopOptions options = KazoDesktopOptions.Default;
        Dictionary<string, KazoDesktopCell> positions = new(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(content))
        {
            return new KazoDesktopSettingsDocument(options, positions);
        }

        foreach (ConfigLine line in ConfigDocumentLines.Parse(content))
        {
            if (line.Kind != ConfigLineKind.KeyValue || line.InMalformedSection || line.Key is not { } key || line.Value is not { } value)
            {
                continue;
            }

            if (line.Group == PositionsGroup)
            {
                if (TryDecodeKey(key, out string name) && KazoDesktopCell.TryParse(value, out KazoDesktopCell cell) && cell.Column >= 0 && cell.Row >= 0)
                {
                    // The last occurrence wins, as for every .config key.
                    positions[name] = cell;
                }
            }
            else if (line.Group == ViewGroup)
            {
                options = ApplyViewValue(options, key, value.Trim());
            }
        }

        return new KazoDesktopSettingsDocument(options, positions);
    }

    /// <summary>Writes the <c>[view]</c> group into <paramref name="content"/>, keeping everything else.</summary>
    /// <param name="content">The file's text, or <see langword="null"/>.</param>
    /// <param name="options">The options to write.</param>
    public static string WithOptions(string? content, KazoDesktopOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string text = content ?? string.Empty;
        text = ConfigDocumentEditor.SetValue(text, ViewGroup, "icon-size", options.IconSize.ToString().ToLowerInvariant());
        text = ConfigDocumentEditor.SetValue(text, ViewGroup, "auto-arrange", Bool(options.AutoArrange));
        text = ConfigDocumentEditor.SetValue(text, ViewGroup, "align-to-grid", Bool(options.AlignToGrid));
        text = ConfigDocumentEditor.SetValue(text, ViewGroup, "sort", options.Sort.ToString().ToLowerInvariant());
        text = ConfigDocumentEditor.SetValue(text, ViewGroup, "show-home", Bool(options.ShowHome));
        return ConfigDocumentEditor.SetValue(text, ViewGroup, "show-trash", Bool(options.ShowTrash));
    }

    /// <summary>Replaces the <c>[positions]</c> group of <paramref name="content"/> with <paramref name="positions"/>.</summary>
    /// <param name="content">The file's text, or <see langword="null"/>.</param>
    /// <param name="positions">Every position to keep; the group holds nothing else afterwards.</param>
    public static string WithPositions(string? content, IReadOnlyDictionary<string, KazoDesktopCell> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        IReadOnlyList<ConfigLine> lines = ConfigDocumentLines.Parse(content ?? string.Empty);
        List<string> kept = [];
        bool inPositions = false;
        foreach (ConfigLine line in lines)
        {
            if (line.Kind == ConfigLineKind.Section)
            {
                inPositions = line.Group == PositionsGroup;
            }
            else if (line.Kind == ConfigLineKind.Malformed && line.Raw.TrimStart().StartsWith('['))
            {
                inPositions = false;
            }

            if (!inPositions)
            {
                kept.Add(line.Raw);
            }
        }

        while (kept.Count > 0 && kept[^1].Trim().Length == 0)
        {
            kept.RemoveAt(kept.Count - 1);
        }

        if (kept.Count > 0)
        {
            kept.Add(string.Empty);
        }

        kept.Add($"[{PositionsGroup}]");
        foreach ((string name, KazoDesktopCell cell) in positions.OrderBy(static pair => pair.Value.Column).ThenBy(static pair => pair.Value.Row).ThenBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            kept.Add(EncodeKey(name) + "=" + cell);
        }

        return string.Join('\n', kept) + "\n";
    }

    /// <summary>Writes a file name as a position key (see the remarks).</summary>
    /// <param name="name">A file name or a <see cref="KazoDesktopItems"/> key.</param>
    public static string EncodeKey(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        StringBuilder key = new(name.Length);
        for (int index = 0; index < name.Length; index++)
        {
            char c = name[index];
            bool edgeSpace = char.IsWhiteSpace(c) && (index == 0 || index == name.Length - 1);
            if (ReservedInKeys.Contains(c) || char.IsControl(c) || edgeSpace)
            {
                foreach (byte b in Encoding.UTF8.GetBytes(c.ToString()))
                {
                    key.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
            }
            else
            {
                key.Append(c);
            }
        }

        return key.ToString();
    }

    /// <summary>Reads a position key; <see langword="false"/> for a malformed <c>%XX</c>.</summary>
    /// <param name="key">The key as written.</param>
    /// <param name="name">The file name.</param>
    public static bool TryDecodeKey(string key, out string name)
    {
        ArgumentNullException.ThrowIfNull(key);
        name = string.Empty;
        if (!key.Contains('%'))
        {
            name = key;
            return key.Length > 0;
        }

        List<byte> bytes = [];
        for (int index = 0; index < key.Length; index++)
        {
            if (key[index] != '%')
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(key[index].ToString()));
                continue;
            }

            if (index + 2 >= key.Length
                || !byte.TryParse(key.AsSpan(index + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte value))
            {
                return false;
            }

            bytes.Add(value);
            index += 2;
        }

        name = Encoding.UTF8.GetString([.. bytes]);
        return name.Length > 0;
    }

    /// <summary>Reads the ambient user's settings; the defaults when the file does not exist or cannot be read.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<KazoDesktopSettingsDocument> ReadAsync(CancellationToken cancellationToken = default) =>
        Parse(await ReadTextAsync(DefaultPath, cancellationToken).ConfigureAwait(false));

    /// <summary>Writes the ambient user's <c>[view]</c> group (Settings ▸ Desktop, the desktop's "View" menu).</summary>
    /// <param name="options">The options.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="UnauthorizedAccessException">The app may not write the user's configuration.</exception>
    public static Task SaveOptionsAsync(KazoDesktopOptions options, CancellationToken cancellationToken = default) =>
        UpdateAsync(content => WithOptions(content, options), cancellationToken);

    /// <summary>Writes the ambient user's <c>[positions]</c> group (the desktop, after a move or a new icon).</summary>
    /// <param name="positions">Every position.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="UnauthorizedAccessException">The app may not write the user's configuration.</exception>
    public static Task SavePositionsAsync(IReadOnlyDictionary<string, KazoDesktopCell> positions, CancellationToken cancellationToken = default) =>
        UpdateAsync(content => WithPositions(content, positions), cancellationToken);

    /// <summary>Gets the ambient user's settings file.</summary>
    public static string DefaultPath => GetPath(KazoUser.Current.HomeDirectory);

    private static async Task UpdateAsync(Func<string?, string> change, CancellationToken cancellationToken)
    {
        string path = DefaultPath;
        string? content = await ReadTextAsync(path, cancellationToken).ConfigureAwait(false);
        string updated = change(content);
        if (updated == content)
        {
            return;
        }

        string directory = path[..path.LastIndexOf('/')];
        if (!await KazoDirectory.ExistsAsync(directory, cancellationToken).ConfigureAwait(false))
        {
            await KazoDirectory.CreateDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
        }

        await KazoFile.WriteAllTextAsync(path, updated, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await KazoFile.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static KazoDesktopOptions ApplyViewValue(KazoDesktopOptions options, string key, string value) => key switch
    {
        "icon-size" when Enum.TryParse(value, ignoreCase: true, out KazoDesktopIconSize size) && Enum.IsDefined(size) => options with { IconSize = size },
        "auto-arrange" when bool.TryParse(value, out bool autoArrange) => options with { AutoArrange = autoArrange },
        "align-to-grid" when bool.TryParse(value, out bool align) => options with { AlignToGrid = align },
        "sort" when Enum.TryParse(value, ignoreCase: true, out KazoDesktopSort sort) && Enum.IsDefined(sort) => options with { Sort = sort },
        "show-home" when bool.TryParse(value, out bool showHome) => options with { ShowHome = showHome },
        "show-trash" when bool.TryParse(value, out bool showTrash) => options with { ShowTrash = showTrash },
        _ => options
    };

    private static string Bool(bool value) => value ? "true" : "false";
}
