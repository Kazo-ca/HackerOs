using System.Text.RegularExpressions;

namespace Kazo.Os.Infrastructure.Browser.Rename;

/// <summary>
/// The names HackerOS gave to what it stored, and the Kazo OS names that replace them (ADR 0228
/// §1, ADR 0230 §3). One table serves every place the rename migration rewrites: IndexedDB
/// database names, <c>localStorage</c>/<c>sessionStorage</c> keys, stored ids, file names of the
/// system areas and the text of the OS configuration files.
/// </summary>
/// <remarks>
/// <para>
/// The rules are the ones work item 5 applied to the source tree (its <c>rename5_persisted</c>
/// table), reduced to what can be found in stored data, in the same order: the display name
/// first (<c>HackerOS</c> → <c>Kazo OS</c>), then the package prefix (<c>HackerOs.</c> →
/// <c>Kazo.Os.</c>), the <c>Hack*</c>/<c>Hacker*</c> type names, and last the lowercase names
/// (<c>hackeros</c> → <c>kazoos</c>, <c>hackweb</c> → <c>kazoweb</c>...).
/// </para>
/// <para>
/// Every rule is idempotent: a text already rewritten holds no old name, so a second pass
/// changes nothing. The migration relies on that to resume after an interruption.
/// </para>
/// </remarks>
internal static partial class RenamedNames
{
    /// <summary>The name of the OS database before the rename.</summary>
    public const string LegacyOsDatabaseName = "hackeros";

    /// <summary>The first version that stores the Kazo OS names (ADR 0230 §2).</summary>
    public static readonly Version RenameVersion = new(3, 0, 0);

    /// <summary>The ordered rewrite rules.</summary>
    private static readonly (Regex Pattern, string Replacement)[] Rules =
    [
        // Display name, in sentences and titles.
        (DisplayName(), "Kazo OS"),
        // Package, assembly and namespace prefix (HackerOs.Apps.Terminal.dll).
        (PackagePrefix(), "Kazo.Os."),
        (ConfigurationSeparator(), "KazoOs__"),
        // Type names: IHackFile, HackerOsVersion, HackPaintWindow.
        (InterfaceTypePrefix(), "IKazo"),
        (HackerTypePrefix(), "Kazo"),
        (HackTypePrefix(), "Kazo"),
        // Lowercase and constant names.
        (new Regex("HACKEROS", RegexOptions.CultureInvariant), "KAZOOS"),
        (new Regex("Hackeros", RegexOptions.CultureInvariant), "Kazoos"),
        (new Regex("hackerOs", RegexOptions.CultureInvariant), "kazoOs"),
        (new Regex("hackeros", RegexOptions.CultureInvariant), "kazoos"),
        (new Regex("HackerOs", RegexOptions.CultureInvariant), "KazoOs"),
        (new Regex("Hackweb", RegexOptions.CultureInvariant), "Kazoweb"),
        (new Regex("hackweb", RegexOptions.CultureInvariant), "kazoweb"),
        (new Regex("hack-paint", RegexOptions.CultureInvariant), "kazo-paint"),
        (new Regex("hackpaint", RegexOptions.CultureInvariant), "kazopaint"),
        (new Regex("hack-store", RegexOptions.CultureInvariant), "kazoos-store"),
        (new Regex(@"\bhackpkg\b", RegexOptions.CultureInvariant), "kazopkg"),
        (new Regex(@"\bhackbackup\b", RegexOptions.CultureInvariant), "kazobackup"),
        (new Regex("hackfileview", RegexOptions.CultureInvariant), "kazofileview"),
        (new Regex("hackbackground", RegexOptions.CultureInvariant), "kazobackground"),
        (new Regex("hacksettings", RegexOptions.CultureInvariant), "kazosettings"),
        // File extensions of packages and theme packs (.hackapp, .hacktheme).
        (new Regex(@"\.hackapp\b", RegexOptions.CultureInvariant), ".kazoapp"),
        (new Regex(@"\.hacktheme\b", RegexOptions.CultureInvariant), ".kazotheme"),
    ];

    /// <summary>Gets whether <paramref name="value"/> holds a name the rename replaced.</summary>
    /// <param name="value">Any stored text.</param>
    public static bool ContainsOldName(string? value) =>
        !string.IsNullOrEmpty(value) && OldName().IsMatch(value);

    /// <summary>Rewrites every old name of <paramref name="value"/> to its Kazo OS name.</summary>
    /// <param name="value">Any stored text: a database name, a key, an id, a file name or a configuration file.</param>
    /// <returns>The rewritten text; the same instance when it holds no old name.</returns>
    public static string Rewrite(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!ContainsOldName(value))
        {
            return value;
        }

        string rewritten = value;
        foreach ((Regex pattern, string replacement) in Rules)
        {
            rewritten = pattern.Replace(rewritten, replacement);
        }

        return rewritten;
    }

    /// <summary>
    /// Gets whether a browser database belongs to HackerOS: the OS database <c>hackeros</c>, its
    /// companions <c>hackeros-*</c> and the site databases <c>hackweb.*</c> (ADR 0228 §1).
    /// </summary>
    /// <param name="databaseName">An IndexedDB database name.</param>
    public static bool IsLegacyDatabaseName(string? databaseName) =>
        databaseName is not null
        && (databaseName == LegacyOsDatabaseName
            || databaseName.StartsWith(LegacyOsDatabaseName + "-", StringComparison.Ordinal)
            || databaseName.StartsWith("hackweb.", StringComparison.Ordinal));

    /// <summary>
    /// Gets whether data written by <paramref name="version"/> predates the rename. A missing or
    /// unreadable version is older (the versions before 3.0.0 recorded none); a pre-release of
    /// 3.0.0 already uses the new names, so only major, minor and patch count.
    /// </summary>
    /// <param name="version">The version that wrote the data, as recorded.</param>
    public static bool PredatesRename(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return true;
        }

        string core = version.Trim().Split('-', '+')[0];
        return !Version.TryParse(core, out Version? parsed)
            || new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0)) < RenameVersion;
    }

    /// <summary>Any old name, whatever its case: what <see cref="ContainsOldName"/> looks for.</summary>
    [GeneratedRegex(@"hacker ?os|hackweb|hack-?paint|hack-store|\bhackpkg\b|\bhackbackup\b|hackfileview|hackbackground|hacksettings|\.hackapp\b|\.hacktheme\b|(?-i:\bI?Hack(?=[A-Z][a-z]))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OldName();

    [GeneratedRegex(@"\bHackerOS\b", RegexOptions.CultureInvariant)]
    private static partial Regex DisplayName();

    [GeneratedRegex(@"\bHackerOs\.(?=[A-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex PackagePrefix();

    [GeneratedRegex("HackerOs__", RegexOptions.CultureInvariant)]
    private static partial Regex ConfigurationSeparator();

    [GeneratedRegex(@"\bIHack(?=[A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex InterfaceTypePrefix();

    [GeneratedRegex(@"\bHacker(?=[A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex HackerTypePrefix();

    [GeneratedRegex(@"\bHack(?=[A-Z][a-z])", RegexOptions.CultureInvariant)]
    private static partial Regex HackTypePrefix();
}
