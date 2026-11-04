using System.Globalization;

namespace Kazo.Os.AppSdk.Globalization;

/// <summary>
/// The locale rules of Kazo OS (ADR 0104): how a process's <c>LANG</c>/<c>LC_*</c> variables map to
/// the culture of its texts and to the culture of its formats, the way the GNU C library does it.
/// </summary>
/// <remarks>
/// <para>
/// Kazo OS ships two languages: English (<c>en</c>, the neutral resources, default and fallback)
/// and French (<c>fr</c>). Any <c>en_*</c> locale gives English texts, any <c>fr_*</c> locale gives
/// French texts, and every other locale (or none) gives English, as a Linux program prints its
/// untranslated messages when no catalog matches.
/// </para>
/// <para>
/// The full locale still drives formats: <c>LANG=fr_CA.UTF-8</c> prints French texts with
/// Canadian French dates and numbers. Lookup order mimics glibc: <c>LC_ALL</c>, then the category
/// (<c>LC_MESSAGES</c> for texts, <c>LC_TIME</c>/<c>LC_NUMERIC</c>/<c>LC_MONETARY</c> for formats),
/// then <c>LANG</c>.
/// </para>
/// <para>
/// Everything here is pure: nothing reads the ambient context. <see cref="HackEnvironment.Culture"/>
/// and <see cref="IAppExecutionContext.Culture"/> apply these rules to a real process.
/// </para>
/// </remarks>
public static class HackLocale
{
    /// <summary>The locale every process starts with when nothing else is configured (ADR 0050).</summary>
    public const string DefaultLocale = "en_US.UTF-8";

    /// <summary>Variable that overrides every category, as in glibc.</summary>
    public const string AllVariable = "LC_ALL";

    /// <summary>Category of translated messages.</summary>
    public const string MessagesVariable = "LC_MESSAGES";

    /// <summary>Category of dates and times.</summary>
    public const string TimeVariable = "LC_TIME";

    /// <summary>Category of number formats.</summary>
    public const string NumericVariable = "LC_NUMERIC";

    /// <summary>Category of currency formats.</summary>
    public const string MonetaryVariable = "LC_MONETARY";

    /// <summary>Default locale of every category.</summary>
    public const string LangVariable = "LANG";

    /// <summary>The neutral language of the resources: English.</summary>
    public static CultureInfo DefaultLanguage { get; } = CultureInfo.GetCultureInfo("en");

    /// <summary>The culture of formats when no locale is set: <c>en-US</c>.</summary>
    public static CultureInfo DefaultFormatCulture { get; } = CultureInfo.GetCultureInfo("en-US");

    /// <summary>The languages Kazo OS has texts for, English first.</summary>
    public static IReadOnlyList<CultureInfo> SupportedLanguages { get; } =
    [
        DefaultLanguage,
        CultureInfo.GetCultureInfo("fr")
    ];

    /// <summary>
    /// The locales <c>localectl list-locales</c> prints and Settings offers, like the generated
    /// locales of a Linux system (<c>/usr/lib/locale</c>).
    /// </summary>
    public static IReadOnlyList<string> AvailableLocales { get; } =
    [
        "C.UTF-8",
        "en_CA.UTF-8",
        "en_GB.UTF-8",
        "en_US.UTF-8",
        "fr_BE.UTF-8",
        "fr_CA.UTF-8",
        "fr_CH.UTF-8",
        "fr_FR.UTF-8"
    ];

    /// <summary>
    /// Every variable a <c>locale.conf</c> file may set (systemd's list): <c>LANG</c>,
    /// <c>LANGUAGE</c> and the <c>LC_*</c> categories. <c>LC_ALL</c> is deliberately absent,
    /// as in systemd: it is for one command line, not for a whole session.
    /// </summary>
    public static IReadOnlySet<string> LocaleConfVariables { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "LANG", "LANGUAGE", "LC_CTYPE", "LC_NUMERIC", "LC_TIME", "LC_COLLATE", "LC_MONETARY",
        "LC_MESSAGES", "LC_PAPER", "LC_NAME", "LC_ADDRESS", "LC_TELEPHONE", "LC_MEASUREMENT",
        "LC_IDENTIFICATION"
    };

    /// <summary>
    /// Returns the language of the texts for a process whose variables are read through
    /// <paramref name="getVariable"/>: <c>en</c> or <c>fr</c>, never a specific culture.
    /// </summary>
    /// <param name="getVariable">Reads one variable of the process; <see langword="null"/> when unset.</param>
    public static CultureInfo ResolveLanguage(Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        return ToLanguage(Effective(getVariable, MessagesVariable));
    }

    /// <summary>
    /// Returns the culture of formats (dates, numbers) for a process: the full culture of its
    /// <paramref name="category"/> (<c>LC_TIME</c> by default), e.g. <c>fr-CA</c>.
    /// </summary>
    /// <param name="getVariable">Reads one variable of the process; <see langword="null"/> when unset.</param>
    /// <param name="category">The <c>LC_*</c> category, <see cref="TimeVariable"/> by default.</param>
    public static CultureInfo ResolveFormatCulture(Func<string, string?> getVariable, string category = TimeVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        return ToCulture(Effective(getVariable, category)) ?? DefaultFormatCulture;
    }

    /// <summary>
    /// Maps a POSIX locale name (<c>fr_CA.UTF-8</c>, <c>en_US</c>, <c>fr</c>, <c>C</c>) to a .NET
    /// culture: <c>C</c> and <c>POSIX</c> give the invariant culture, an unknown or malformed name
    /// gives <see langword="null"/>.
    /// </summary>
    /// <param name="locale">The value of <c>LANG</c> or of an <c>LC_*</c> variable.</param>
    public static CultureInfo? ToCulture(string? locale)
    {
        string? name = Normalize(locale);
        if (name is null)
        {
            return null;
        }

        if (name is "C" or "POSIX")
        {
            return CultureInfo.InvariantCulture;
        }

        try
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(name);
            // ICU accepts any well-formed tag: "zz-QQ" comes back with no ISO 639-2 language code, and
            // "fr-ZZ" as "French (Unknown Region)". Neither is a locale glibc would have installed.
            return culture.ThreeLetterISOLanguageName.Length == 0
                   || culture.EnglishName.Contains("Unknown", StringComparison.Ordinal)
                ? null
                : culture;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps a POSIX locale name to one of <see cref="SupportedLanguages"/>: French for any
    /// <c>fr</c>/<c>fr_*</c> locale, English for everything else (including <c>C</c> and garbage).
    /// </summary>
    /// <param name="locale">The value of <c>LANG</c>, <c>LC_MESSAGES</c> or <c>LC_ALL</c>.</param>
    public static CultureInfo ToLanguage(string? locale)
    {
        string? name = Normalize(locale);
        if (name is null)
        {
            return DefaultLanguage;
        }

        string language = name.Split('-')[0];
        return SupportedLanguages.FirstOrDefault(supported =>
                   string.Equals(supported.TwoLetterISOLanguageName, language, StringComparison.OrdinalIgnoreCase))
               ?? DefaultLanguage;
    }

    /// <summary>
    /// Formats a .NET culture as the POSIX locale Kazo OS writes in <c>locale.conf</c>:
    /// <c>fr-CA</c> becomes <c>fr_CA.UTF-8</c>, the invariant culture <c>C.UTF-8</c>.
    /// </summary>
    /// <param name="culture">A specific or invariant culture.</param>
    public static string ToLocaleName(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return culture.Name.Length == 0
            ? "C.UTF-8"
            : $"{culture.Name.Replace('-', '_')}.UTF-8";
    }

    /// <summary>
    /// Returns whether <paramref name="locale"/> is a locale Kazo OS knows: one of
    /// <see cref="AvailableLocales"/>, compared without the charset (<c>fr_CA</c> and
    /// <c>fr_CA.utf8</c> both match <c>fr_CA.UTF-8</c>).
    /// </summary>
    /// <param name="locale">A POSIX locale name.</param>
    public static bool IsAvailable(string? locale)
    {
        string? name = Normalize(locale);
        return name is not null && AvailableLocales.Any(available =>
            string.Equals(Normalize(available), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parses a <c>locale.conf</c> file (systemd format: <c>NAME=value</c> lines, <c>#</c>
    /// comments, optional quotes). Lines that are malformed or name a variable outside
    /// <see cref="LocaleConfVariables"/> are skipped and reported through <paramref name="warnings"/>.
    /// </summary>
    /// <param name="content">File content, or <see langword="null"/> when the file is absent.</param>
    /// <param name="warnings">Receives one message per ignored line (1-based line number and text).</param>
    /// <returns>The variables, in file order; the last assignment of a name wins.</returns>
    public static IReadOnlyDictionary<string, string> ParseLocaleConf(string? content, ICollection<(int Line, string Message)>? warnings = null)
    {
        Dictionary<string, string> variables = new(StringComparer.Ordinal);
        if (content is null)
        {
            return variables;
        }

        string[] lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                warnings?.Add((index + 1, "ignored invalid line (expected NAME=value)"));
                continue;
            }

            string name = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }

            if (!LocaleConfVariables.Contains(name))
            {
                warnings?.Add((index + 1, $"{name} is not a locale variable, line ignored"));
                continue;
            }

            if (value.Contains('\0') || value.Contains('\n'))
            {
                warnings?.Add((index + 1, $"{name} has an invalid value, line ignored"));
                continue;
            }

            variables[name] = value;
        }

        return variables;
    }

    /// <summary>
    /// Writes a <c>locale.conf</c> file holding <paramref name="variables"/>, sorted with
    /// <c>LANG</c> first, in the format <see cref="ParseLocaleConf"/> reads.
    /// </summary>
    /// <param name="variables">Locale variables; names outside <see cref="LocaleConfVariables"/> are rejected.</param>
    /// <param name="header">Optional comment line written first (without the leading <c>#</c>).</param>
    /// <exception cref="ArgumentException">A name is not a locale variable or a value is unsafe.</exception>
    public static string FormatLocaleConf(IEnumerable<KeyValuePair<string, string>> variables, string? header = null)
    {
        ArgumentNullException.ThrowIfNull(variables);
        System.Text.StringBuilder builder = new();
        if (!string.IsNullOrWhiteSpace(header))
        {
            builder.Append("# ").Append(header.Trim()).Append('\n');
        }

        foreach (KeyValuePair<string, string> variable in variables
                     .OrderBy(pair => pair.Key == LangVariable ? 0 : 1)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!LocaleConfVariables.Contains(variable.Key))
            {
                throw new ArgumentException($"'{variable.Key}' is not a locale variable.", nameof(variables));
            }

            if (variable.Value.Contains('\n') || variable.Value.Contains('\0') || variable.Value.Contains('='))
            {
                throw new ArgumentException($"The value of '{variable.Key}' is not a locale name.", nameof(variables));
            }

            builder.Append(variable.Key).Append('=').Append(variable.Value).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>glibc's lookup: <c>LC_ALL</c>, then the category, then <c>LANG</c>; empty values count as unset.</summary>
    private static string? Effective(Func<string, string?> getVariable, string category)
    {
        foreach (string name in (ReadOnlySpan<string>)[AllVariable, category, LangVariable])
        {
            if (getVariable(name) is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Turns <c>fr_CA.UTF-8@euro</c> into the .NET name <c>fr-CA</c>: the charset and the modifier
    /// are dropped, the separator becomes <c>-</c>. Returns <see langword="null"/> for an empty or
    /// malformed name.
    /// </summary>
    private static string? Normalize(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return null;
        }

        string name = locale.Trim();
        int cut = name.IndexOfAny(['.', '@']);
        if (cut >= 0)
        {
            name = name[..cut];
        }

        if (name.Length == 0 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
        {
            return null;
        }

        return name.Replace('_', '-');
    }
}
