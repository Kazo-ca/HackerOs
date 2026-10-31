using System.Globalization;

namespace HackerOs.Simulation.Abstractions.Sounds;

/// <summary>
/// The <c>index.theme</c> file of a sound theme (ADR 0204 section 1), the freedesktop.org format:
/// </summary>
/// <remarks>
/// <code>
/// [Sound Theme]
/// Name=HackerOS
/// Name[fr]=HackerOS
/// Comment=Console bleeps
/// Directories=stereo
/// </code>
/// Only the <c>[Sound Theme]</c> group is read; other groups (the per-directory
/// <c>[stereo]</c> groups of the specification) are skipped. <c>Directories</c> lists the
/// sub-folders that hold the sound files, searched in order; the theme folder itself is searched
/// last. A missing or empty <c>Name</c> falls back to the theme identifier.
/// </remarks>
public sealed class SoundThemeIndex
{
    private const string GroupName = "Sound Theme";

    private readonly IReadOnlyDictionary<string, string> _names;
    private readonly IReadOnlyDictionary<string, string> _comments;

    private SoundThemeIndex(
        string id,
        IReadOnlyDictionary<string, string> names,
        IReadOnlyDictionary<string, string> comments,
        IReadOnlyList<string> directories)
    {
        Id = id;
        _names = names;
        _comments = comments;
        Directories = directories;
    }

    /// <summary>Gets the theme identifier: the name of its folder.</summary>
    public string Id { get; }

    /// <summary>Gets the sub-folders that hold the sound files, in search order.</summary>
    public IReadOnlyList<string> Directories { get; }

    /// <summary>Gets the untranslated name (<c>Name=</c>), or the identifier.</summary>
    public string Name => _names.TryGetValue(string.Empty, out string? name) ? name : Id;

    /// <summary>Parses the content of an <c>index.theme</c> file.</summary>
    /// <param name="id">The theme identifier (its folder name).</param>
    /// <param name="content">The file content; <see langword="null"/> or empty gives a theme named after its identifier.</param>
    public static SoundThemeIndex Parse(string id, string? content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> comments = new(StringComparer.OrdinalIgnoreCase);
        List<string> directories = [];
        bool inGroup = false;

        foreach (string rawLine in (content ?? string.Empty).Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                inGroup = string.Equals(line[1..^1].Trim(), GroupName, StringComparison.Ordinal);
                continue;
            }

            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (!inGroup || equals <= 0)
            {
                continue;
            }

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();
            if (TryLocalized(key, "Name", out string? locale) && value.Length > 0)
            {
                names[locale] = value;
            }
            else if (TryLocalized(key, "Comment", out locale) && value.Length > 0)
            {
                comments[locale] = value;
            }
            else if (key == "Directories")
            {
                directories.AddRange(value
                    .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(IsSafeDirectory));
            }
        }

        return new SoundThemeIndex(id, names, comments, directories);
    }

    /// <summary>Gets the name in a language: <c>Name[fr_CA]</c>, then <c>Name[fr]</c>, then <c>Name</c>.</summary>
    /// <param name="culture">The language of the reader.</param>
    public string GetName(CultureInfo culture) => Localized(_names, culture) ?? Id;

    /// <summary>Gets the description in a language, or <see langword="null"/> when the file has none.</summary>
    /// <param name="culture">The language of the reader.</param>
    public string? GetComment(CultureInfo culture) => Localized(_comments, culture);

    /// <summary>Gets the folders searched for the sound files of the theme, under <paramref name="themeDirectory"/>.</summary>
    /// <param name="themeDirectory">The theme folder (<c>/usr/share/sounds/hackeros</c>).</param>
    public IEnumerable<string> GetSearchDirectories(string themeDirectory)
    {
        string root = themeDirectory.TrimEnd('/');
        foreach (string directory in Directories)
        {
            yield return root + "/" + directory;
        }

        yield return root;
    }

    private static string? Localized(IReadOnlyDictionary<string, string> values, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        string full = culture.Name.Replace('-', '_');
        if (full.Length > 0 && values.TryGetValue(full, out string? value))
        {
            return value;
        }

        if (culture.TwoLetterISOLanguageName is { Length: > 0 } language
            && culture.Name.Length > 0
            && values.TryGetValue(language, out value))
        {
            return value;
        }

        return values.TryGetValue(string.Empty, out value) ? value : null;
    }

    // "Name" -> locale "", "Name[fr]" -> locale "fr".
    private static bool TryLocalized(string key, string name, out string locale)
    {
        locale = string.Empty;
        if (key == name)
        {
            return true;
        }

        if (key.Length > name.Length + 2
            && key.StartsWith(name + "[", StringComparison.Ordinal)
            && key[^1] == ']')
        {
            locale = key[(name.Length + 1)..^1];
            return true;
        }

        return false;
    }

    // A sub-folder name only: no "..", no absolute path, no nested path.
    private static bool IsSafeDirectory(string directory) =>
        directory.Length > 0
        && directory != "."
        && directory != ".."
        && !directory.Contains('/', StringComparison.Ordinal)
        && !directory.Contains('\\', StringComparison.Ordinal);
}
