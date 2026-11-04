using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kazo.Os.Theming.Abstractions;

/// <summary>One warning or error found while reading a <c>.theme</c> file.</summary>
/// <param name="Line">1-based line number, or <see langword="null"/> for the whole file.</param>
/// <param name="Message">Human-readable reason, in English; <see cref="GetMessage"/> translates it.</param>
/// <param name="IsError"><see langword="true"/> when the whole theme is refused; otherwise the line is ignored.</param>
/// <param name="Path">File path, when known.</param>
public sealed record ThemeFileDiagnostic(int? Line, string Message, bool IsError, string? Path = null)
{
    /// <summary>
    /// The reason as a resource key and arguments, when the diagnostic was produced by this package,
    /// so it can be shown in the reader's language; <see langword="null"/> for a diagnostic created
    /// with a plain <see cref="Message"/>.
    /// </summary>
    internal ThemeText? Text { get; init; }

    /// <summary>Gets the reason in a language (ADR 0104).</summary>
    /// <param name="culture">
    /// Language of the reader: an app passes <c>AppContext.Culture</c>, the platform
    /// <c>SessionLocaleService.Culture</c>; <see langword="null"/> returns <see cref="Message"/>.
    /// </param>
    public string GetMessage(CultureInfo? culture) => culture is null || Text is null ? Message : Text.Format(culture);

    /// <summary>
    /// Formats the diagnostic as <c>path:line: warning: reason</c> in a language, for display. The
    /// invariant <see cref="ToString()"/> stays English: it is the form written to the logs.
    /// </summary>
    /// <param name="culture">Language of the reader; <see langword="null"/> gives <see cref="ToString()"/>.</param>
    public string ToString(CultureInfo? culture) => culture is null
        ? ToString()
        : Format(
            Path ?? Strings.Format(culture, "Diagnostic.NoPath"),
            Strings.Format(culture, IsError ? "Diagnostic.Error" : "Diagnostic.Warning"),
            GetMessage(culture));

    /// <inheritdoc />
    public override string ToString() => Format(Path ?? "theme", IsError ? "error" : "warning", Message);

    /// <summary>Creates a diagnostic whose reason is a translatable text; <see cref="Message"/> holds its English form.</summary>
    internal static ThemeFileDiagnostic Create(int? line, ThemeText text, bool isError, string? path) =>
        new(line, text.Format(null), isError, path) { Text = text };

    private string Format(string path, string severity, string message) =>
        $"{path}{(Line is { } line ? $":{line}" : string.Empty)}: {severity}: {message}";
}

/// <summary>Result of <see cref="ThemeFile.Parse"/>.</summary>
/// <param name="Theme">The theme, or <see langword="null"/> when an error refused it.</param>
/// <param name="Diagnostics">Every warning and error, in line order.</param>
public sealed record ThemeFileParseResult(UserTheme? Theme, IReadOnlyList<ThemeFileDiagnostic> Diagnostics);

/// <summary>
/// Reads and writes the <c>.theme</c> format of ADR 0103 §2.2: the line-based format of the settings
/// files (ADR 0044) with a <c>[theme]</c> section (<c>id</c>, <c>name</c>, <c>base</c>,
/// <c>platforms</c>) and a <c>[tokens]</c> section of verified token values.
/// </summary>
/// <remarks>
/// Like the settings files, reading is tolerant (ADR 0044 §5): an unknown token, an invalid value or
/// a malformed line is ignored with a warning and the rest of the file still applies. Only a missing
/// or invalid identity (<c>id</c>, <c>name</c>, <c>base</c>) refuses the whole theme.
/// </remarks>
public static partial class ThemeFile
{
    /// <summary>File extension of theme files, as <c>index.theme</c> on freedesktop.</summary>
    public const string Extension = ".theme";

    /// <summary>Media type used for theme files in file associations.</summary>
    public const string MediaType = "text/x-hackeros-theme";

    /// <summary>Largest theme file accepted, in bytes (64 KiB): a theme is a short list of values.</summary>
    public const int MaxFileBytes = 64 * 1024;

    /// <summary>Longest display name, in characters.</summary>
    public const int MaxNameLength = 80;

    private const string ThemeSection = "theme";
    private const string TokensSection = "tokens";

    /// <summary>Checks a theme identifier: lowercase letters, digits and dashes, 1 to 64 characters.</summary>
    /// <param name="id">Candidate identifier.</param>
    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>Parses a theme file.</summary>
    /// <param name="content">Whole file content.</param>
    /// <param name="origin">Folder the file came from.</param>
    /// <param name="path">File path, for diagnostics and <see cref="UserTheme.SourcePath"/>.</param>
    public static ThemeFileParseResult Parse(string content, ThemeOrigin origin = ThemeOrigin.User, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        List<ThemeFileDiagnostic> diagnostics = [];
        void Warn(int? line, string key, params object?[] arguments) =>
            diagnostics.Add(ThemeFileDiagnostic.Create(line, new ThemeText(key, arguments), isError: false, path));
        ThemeFileParseResult Fail(string key, params object?[] arguments)
        {
            diagnostics.Add(ThemeFileDiagnostic.Create(null, new ThemeText(key, arguments), isError: true, path));
            return new ThemeFileParseResult(null, diagnostics);
        }

        if (Encoding.UTF8.GetByteCount(content) > MaxFileBytes)
        {
            return Fail("File.TooLarge", MaxFileBytes / 1024);
        }

        string? id = null;
        string? name = null;
        string? baseThemeId = null;
        string? platformsValue = null;
        int platformsLine = 0;
        List<KeyValuePair<string, string>> tokens = [];
        string? section = null;
        string[] lines = content.ReplaceLineEndings("\n").Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            int lineNumber = index + 1;
            string line = lines[index].Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] == '[')
            {
                if (line[^1] != ']')
                {
                    Warn(lineNumber, "File.MalformedSection");
                    section = null;
                    continue;
                }

                section = line[1..^1].Trim().ToLowerInvariant();
                if (section is not (ThemeSection or TokensSection))
                {
                    Warn(lineNumber, "File.UnknownSection", section);
                }

                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0)
            {
                Warn(lineNumber, "File.ExpectedKeyValue");
                continue;
            }

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();
            switch (section)
            {
                case ThemeSection:
                    switch (key)
                    {
                        case "id": id = value; break;
                        case "name": name = value; break;
                        case "base": baseThemeId = value; break;
                        case "platforms": platformsValue = value; platformsLine = lineNumber; break;
                        default: Warn(lineNumber, "File.UnknownKey", key); break;
                    }

                    break;
                case TokensSection:
                    if (!ThemeTokenValidator.Check(key, value, out string normalized, out ThemeText? error))
                    {
                        Warn(lineNumber, "File.TokenIgnored", error, key);
                        break;
                    }

                    int existing = tokens.FindIndex(pair => pair.Key == key);
                    if (existing >= 0)
                    {
                        Warn(lineNumber, "File.TokenTwice", key);
                        tokens.RemoveAt(existing);
                    }

                    tokens.Add(new(key, normalized));
                    break;
                case null:
                    Warn(lineNumber, "File.KeyOutsideSection");
                    break;
                default:
                    // Unknown section, already reported on its header.
                    break;
            }
        }

        if (!IsValidId(id))
        {
            return id is null ? Fail("File.MissingId") : Fail("File.InvalidId", id);
        }

        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength || name.Any(char.IsControl))
        {
            return Fail("File.InvalidName", MaxNameLength);
        }

        if (!ThemeCatalog.TryGet(baseThemeId, out ThemeDefinition? baseTheme))
        {
            return baseThemeId is null
                ? Fail("File.MissingBase")
                : Fail("File.UnknownBase", baseThemeId, WellKnownThemeIds.HackerOs);
        }

        List<ThemePlatform> platforms = [];
        if (platformsValue is null)
        {
            platforms.Add(baseTheme.Platform);
        }
        else
        {
            foreach (string part in platformsValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                ThemePlatform? platform = part.ToLowerInvariant() switch
                {
                    "desktop" => ThemePlatform.Desktop,
                    "mobile" => ThemePlatform.Mobile,
                    _ => null
                };
                if (platform is null)
                {
                    Warn(platformsLine, "File.UnknownPlatform", part);
                }
                else if (!platforms.Contains(platform.Value))
                {
                    platforms.Add(platform.Value);
                }
            }

            if (platforms.Count == 0)
            {
                Warn(platformsLine, "File.NoPlatform", FormatPlatform(baseTheme.Platform));
                platforms.Add(baseTheme.Platform);
            }
        }

        return new ThemeFileParseResult(
            new UserTheme(id!, name, baseTheme.Id, platforms, tokens, origin, path),
            diagnostics);
    }

    /// <summary>Writes a theme in the canonical <c>.theme</c> layout, tokens in file order.</summary>
    /// <param name="theme">Theme to write.</param>
    public static string Serialize(UserTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        StringBuilder builder = new();
        builder.Append("# Kazo OS theme (ADR 0103). Only the tokens listed in the Theme Editor's documentation are read.\n");
        builder.Append("# Missing tokens keep the value of the base theme.\n\n");
        builder.Append('[').Append(ThemeSection).Append("]\n");
        builder.Append("id = ").Append(theme.Id).Append('\n');
        builder.Append("name = ").Append(theme.Name).Append('\n');
        builder.Append("base = ").Append(theme.BaseThemeId).Append('\n');
        builder.Append("platforms = ").Append(string.Join(", ", theme.Platforms.Select(FormatPlatform))).Append("\n\n");
        builder.Append('[').Append(TokensSection).Append("]\n");
        foreach ((string key, string value) in theme.Tokens)
        {
            builder.Append(key).Append(" = ").Append(value).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Gets the lowercase name of a platform as written in <c>platforms</c>.</summary>
    /// <param name="platform">Platform.</param>
    public static string FormatPlatform(ThemePlatform platform) => platform == ThemePlatform.Mobile ? "mobile" : "desktop";

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$")]
    private static partial Regex IdPattern();
}
