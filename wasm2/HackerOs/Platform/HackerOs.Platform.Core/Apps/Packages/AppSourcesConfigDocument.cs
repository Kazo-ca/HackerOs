using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.Settings;

namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>
/// <c>/etc/hackeros/app-sources.config</c> (ADR 0182 §3, ADR 0189 §1): the hosts the OS itself
/// contacts to download app packages, the catalogs it reads, the trusted sources and how often it
/// looks for updates.
/// </summary>
/// <remarks>
/// <code>
/// [hosts]
/// github=raw.githubusercontent.com
/// [catalogs]
/// official=https://raw.githubusercontent.com/Kazo-ca/HackerOs-AppMarketPlace/packages/catalog.json
/// [trusted]
/// catalog:official=true
/// github.com/Kazo-ca=true
/// [updates]
/// interval-hours=6
/// [signing-keys]
/// catalog:official=MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE…
/// </code>
/// Read line by line without a schema, like <see cref="AppsConfigDocument"/>: a bad line is skipped
/// with a warning, the last occurrence of a key wins. The content written when the file does not
/// exist comes from the host's build (<see cref="AppPackageDefaults"/>).
/// </remarks>
public static class AppSourcesConfigDocument
{
    /// <summary>Section naming the download host of each source kind.</summary>
    public const string HostsSection = "hosts";

    /// <summary>Section naming each catalog (ADR 0188) by the address of its <c>catalog.json</c>.</summary>
    public const string CatalogsSection = "catalogs";

    /// <summary>Section listing the trusted sources, as <c>&lt;key&gt;=true</c>.</summary>
    public const string TrustedSection = "trusted";

    /// <summary>Section of the update check.</summary>
    public const string UpdatesSection = "updates";

    /// <summary>
    /// Section of the public keys whose signature a source's packages must carry (ADR 0214 §2):
    /// <c>&lt;trust key&gt;=&lt;base64 key&gt;[,&lt;base64 key&gt;…]</c>.
    /// </summary>
    public const string SigningKeysSection = "signing-keys";

    /// <summary>Key of <see cref="UpdatesSection"/>: hours between two update checks.</summary>
    public const string IntervalHoursKey = "interval-hours";

    /// <summary>Key of <see cref="HostsSection"/> for GitHub sources.</summary>
    public const string GitHubHostKey = "github";

    /// <summary>Download host used when the file is absent, or names no GitHub host.</summary>
    public const string DefaultGitHubHost = "raw.githubusercontent.com";

    /// <summary>Owner trusted by default (ADR 0182, answer A to Q3; ADR 0189, answer B to Q24).</summary>
    public const string DefaultTrustedKey = "github.com/Kazo-ca";

    /// <summary>Prefix of a catalog's key in <see cref="TrustedSection"/>, e.g. <c>catalog:official</c>.</summary>
    public const string CatalogTrustPrefix = "catalog:";

    /// <summary>File name under <c>/etc/hackeros</c>.</summary>
    public const string FileName = "app-sources.config";

    /// <summary>Longest update interval accepted: 30 days.</summary>
    internal const int MaximumIntervalHours = 24 * 30;

    /// <summary>Machine file, <c>system:system 0644</c>.</summary>
    public static VirtualPath MachinePath { get; } = VirtualPath.Parse($"/etc/hackeros/{FileName}");

    /// <summary>Content written when the file does not exist yet, with the standard defaults.</summary>
    public static string DefaultContent { get; } = BuildDefaultContent(AppPackageDefaults.Standard);

    /// <summary>Builds the content written when the file does not exist yet.</summary>
    /// <param name="defaults">The sources the host was built with.</param>
    public static string BuildDefaultContent(AppPackageDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        StringBuilder text = new();
        text.Append("# Where the OS downloads app packages, and which sources it trusts (ADR 0182, ADR 0189).\n")
            .Append("# hackpkg contacts only the hosts below, itself, never through an app.\n")
            .Append('[').Append(HostsSection).Append("]\n")
            .Append(GitHubHostKey).Append('=').Append(DefaultGitHubHost).Append('\n')
            .Append('\n')
            .Append("# Catalogs (ADR 0188). Name = address of catalog.json.\n")
            .Append('[').Append(CatalogsSection).Append("]\n");
        foreach ((string name, Uri address) in defaults.Catalogs)
        {
            text.Append(name).Append('=').Append(address.AbsoluteUri).Append('\n');
        }

        text.Append('\n')
            .Append("# Trusted sources: their apps install for one user without an administrator.\n")
            .Append("# Any other source needs an administrator's two-step approval (ADR 0189 section 4).\n")
            .Append('[').Append(TrustedSection).Append("]\n");
        foreach (string key in defaults.TrustedKeys)
        {
            text.Append(key).Append("=true\n");
        }

        text.Append('\n')
            .Append('[').Append(UpdatesSection).Append("]\n")
            .Append(IntervalHoursKey).Append('=').Append(defaults.UpdateIntervalHours.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append('\n')
            .Append("# Public keys (base64 SubjectPublicKeyInfo, ECDSA P-256) a source's packages must be signed with\n")
            .Append("# (ADR 0214 section 2). A source listed here refuses any unsigned or badly signed package.\n")
            .Append('[').Append(SigningKeysSection).Append("]\n");
        foreach (IGrouping<string, KeyValuePair<string, string>> keys in defaults.SigningKeys.GroupBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            text.Append(keys.Key).Append('=').AppendJoin(',', keys.Select(static pair => pair.Value)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Reads the file with the standard defaults. Never fails: an absent file gives the defaults.</summary>
    /// <param name="content">File content, or <see langword="null"/> when the file is absent or unreadable.</param>
    public static AppSourcesConfig Read(string? content) => Read(content, AppPackageDefaults.Standard);

    /// <summary>Reads the file. Never fails: an absent file gives the host's defaults.</summary>
    /// <param name="content">File content, or <see langword="null"/> when the file is absent or unreadable.</param>
    /// <param name="defaults">The sources the host was built with; they stay "built in" (ADR 0189 §1).</param>
    public static AppSourcesConfig Read(string? content, AppPackageDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (content is null)
        {
            return Read(BuildDefaultContent(defaults), defaults);
        }

        Dictionary<string, string> hosts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Uri> catalogs = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, bool> trusted = new(StringComparer.OrdinalIgnoreCase);
        int interval = defaults.UpdateIntervalHours;
        Dictionary<string, List<byte[]>> signingKeys = new(StringComparer.OrdinalIgnoreCase);
        List<string> warnings = [];
        foreach (ConfigLine line in ConfigDocumentLines.Parse(content))
        {
            if (line.Kind != ConfigLineKind.KeyValue || line.InMalformedSection || line.Key is not { Length: > 0 } key)
            {
                continue;
            }

            string value = (line.Value ?? string.Empty).Trim();
            string where = $"apps: {MachinePath.Value}:{line.LineNumber}";
            switch (line.Group)
            {
                case HostsSection when IsHostName(value):
                    hosts[key] = value.ToLowerInvariant();
                    break;
                case HostsSection:
                    warnings.Add($"{where}: '{value}' is not a host name, line ignored");
                    break;
                case CatalogsSection when IsCatalogName(key) && TryParseCatalogAddress(value, out Uri? address):
                    catalogs[key] = address;
                    break;
                case CatalogsSection:
                    warnings.Add($"{where}: '{key}={value}' is not a catalog name and an https address, line ignored");
                    break;
                case TrustedSection when TryParseBoolean(value, out bool isTrue) && IsTrustKey(key):
                    trusted[key] = isTrue;
                    break;
                case TrustedSection:
                    warnings.Add($"{where}: '{key}={value}' is not a source set to true or false, line ignored");
                    break;
                case UpdatesSection when string.Equals(key, IntervalHoursKey, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int hours)
                    && hours is > 0 and <= MaximumIntervalHours:
                    interval = hours;
                    break;
                case UpdatesSection:
                    warnings.Add($"{where}: '{key}={value}' is not interval-hours between 1 and {MaximumIntervalHours}, line ignored");
                    break;
                case SigningKeysSection when IsTrustKey(key):
                    List<byte[]> parsed = [];
                    foreach (string item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (AppPackageSignature.TryParsePublicKey(item, out byte[]? publicKey))
                        {
                            parsed.Add(publicKey);
                        }
                        else
                        {
                            warnings.Add($"{where}: '{item}' is not the base64 of an ECDSA P-256 public key, key ignored");
                        }
                    }

                    signingKeys[key] = parsed;
                    break;
                case SigningKeysSection:
                    warnings.Add($"{where}: '{key}' is not a source, line ignored");
                    break;
            }
        }

        return new AppSourcesConfig(
            hosts.TryGetValue(GitHubHostKey, out string? gitHub) ? gitHub : DefaultGitHubHost,
            trusted.Where(static pair => pair.Value).Select(static pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase),
            warnings)
        {
            Catalogs = catalogs,
            UntrustedKeys = trusted.Where(static pair => !pair.Value).Select(static pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase),
            UpdateIntervalHours = interval,
            BuiltInKeys = defaults.TrustedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            SigningKeys = MergeSigningKeys(signingKeys, defaults),
        };
    }

    /// <summary>
    /// Adds a source (ADR 0189 §1, <c>hackpkg sources add</c>), keeping every other line exactly.
    /// A catalog goes to <c>[catalogs]</c>; every source gets its line in <c>[trusted]</c>, set to
    /// <see langword="false"/> unless the administrator chose otherwise (Q25 answer A).
    /// </summary>
    /// <param name="content">Current file content, or <see langword="null"/> for a new file.</param>
    /// <param name="source">The parsed source.</param>
    /// <param name="trusted">Whether its apps install for one user without an administrator.</param>
    public static string AddSource(string? content, AppSourceSpecification source, bool trusted)
    {
        ArgumentNullException.ThrowIfNull(source);
        string edited = string.IsNullOrEmpty(content) ? DefaultContent : content;
        if (source.CatalogAddress is { } address)
        {
            edited = ConfigDocumentEditor.SetValue(edited, CatalogsSection, source.Name, address.AbsoluteUri);
        }

        return ConfigDocumentEditor.SetValue(edited, TrustedSection, source.TrustKey, trusted ? "true" : "false");
    }

    /// <summary>Removes a source and its trust line, keeping every other line exactly.</summary>
    /// <param name="content">Current file content.</param>
    /// <param name="source">The parsed source.</param>
    public static string RemoveSource(string? content, AppSourceSpecification source)
    {
        ArgumentNullException.ThrowIfNull(source);
        string edited = content ?? string.Empty;
        if (source.IsCatalog)
        {
            edited = ConfigDocumentEditor.RemoveValue(edited, CatalogsSection, source.Name);
        }

        return ConfigDocumentEditor.RemoveValue(edited, TrustedSection, source.TrustKey);
    }

    /// <summary>The keys of the file, plus the host's own (ADR 0214 §2: built-in keys cannot be dropped by editing the file).</summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<byte[]>> MergeSigningKeys(
        Dictionary<string, List<byte[]>> fromFile, AppPackageDefaults defaults)
    {
        foreach ((string source, string text) in defaults.SigningKeys)
        {
            if (!AppPackageSignature.TryParsePublicKey(text, out byte[]? key))
            {
                continue;
            }

            if (!fromFile.TryGetValue(source, out List<byte[]>? keys))
            {
                fromFile[source] = keys = [];
            }

            if (!keys.Any(existing => existing.AsSpan().SequenceEqual(key)))
            {
                keys.Add(key);
            }
        }

        return fromFile
            .Where(static pair => pair.Value.Count > 0)
            .ToDictionary(static pair => pair.Key, static pair => (IReadOnlyList<byte[]>)pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gets whether <paramref name="key"/> names a catalog or a GitHub owner or repository.</summary>
    internal static bool IsTrustKey(string key) =>
        (key.StartsWith(CatalogTrustPrefix, StringComparison.OrdinalIgnoreCase) || key.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
        && AppSourceSpecification.TryParse(key, out AppSourceSpecification? parsed)
        && string.Equals(parsed.TrustKey, key, StringComparison.OrdinalIgnoreCase);

    /// <summary>A catalog name: lower-case letters, digits and inner dashes, 1 to 64 characters.</summary>
    internal static bool IsCatalogName(string name) =>
        name.Length is > 0 and <= 64
        && name.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
        && !name.StartsWith('-')
        && !name.EndsWith('-');

    /// <summary>A catalog address: an absolute <c>https</c> URL with no user information.</summary>
    internal static bool TryParseCatalogAddress(string value, [NotNullWhen(true)] out Uri? address)
    {
        address = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !IsHostName(parsed.Host))
        {
            return false;
        }

        address = parsed;
        return true;
    }

    private static bool TryParseBoolean(string value, out bool result)
    {
        result = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        return result || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHostName(string value) =>
        value.Length is > 0 and <= 253
        && Uri.CheckHostName(value) == UriHostNameType.Dns
        && value.Contains('.', StringComparison.Ordinal);
}

/// <summary>Trust level of a source as the Store and <c>hackpkg</c> show it (ADR 0189 §2).</summary>
public enum AppSourceTrust
{
    /// <summary>An app of the official catalog, reviewed and compiled by the HackerOS team.</summary>
    Verified = 0,

    /// <summary>Another source compiled into the OS, or marked trusted by an administrator.</summary>
    Trusted = 1,

    /// <summary>Any other source: every install asks an administrator.</summary>
    Untrusted = 2
}

/// <summary>What <c>app-sources.config</c> allows.</summary>
/// <param name="GitHubHost">Host the OS downloads GitHub packages from.</param>
/// <param name="TrustedKeys">
/// Trusted keys, compared without case: <c>catalog:&lt;name&gt;</c>, <c>github.com/&lt;owner&gt;</c>
/// or <c>github.com/&lt;owner&gt;/&lt;repository&gt;</c>.
/// </param>
/// <param name="Warnings">One <c>/var/log/syslog</c> line per ignored line.</param>
public sealed record AppSourcesConfig(string GitHubHost, IReadOnlySet<string> TrustedKeys, IReadOnlyList<string> Warnings)
{
    /// <summary>Name of the official catalog (ADR 0188): its apps are <see cref="AppSourceTrust.Verified"/>.</summary>
    public const string OfficialCatalogName = "official";

    /// <summary>Gets the catalogs by name.</summary>
    public IReadOnlyDictionary<string, Uri> Catalogs { get; init; } = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the sources an administrator added without trusting them (<c>=false</c>).</summary>
    public IReadOnlySet<string> UntrustedKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the hours between two update checks.</summary>
    public int UpdateIntervalHours { get; init; } = AppPackageDefaults.DefaultUpdateIntervalHours;

    /// <summary>Gets the trusted keys the host was built with (ADR 0189 §1), compared without case.</summary>
    public IReadOnlySet<string> BuiltInKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the public keys (SubjectPublicKeyInfo) by trust key of <c>[signing-keys]</c>, the host's
    /// own included (ADR 0214 §2).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<byte[]>> SigningKeys { get; init; } =
        new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the keys one of which must have signed every package of <paramref name="source"/>
    /// (ADR 0214 §2): those of its catalog, or of its GitHub owner and of the repository itself.
    /// Empty when the source has none: its packages are then accepted on their SHA-256 alone.
    /// </summary>
    public IReadOnlyList<byte[]> GetSigningKeys(AppPackageSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        string[] keys = source.Catalog is { } catalog
            ? [AppSourcesConfigDocument.CatalogTrustPrefix + catalog]
            : [source.TrustKey, source.RepositoryTrustKey];
        return [.. keys.Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(key => SigningKeys.TryGetValue(key, out IReadOnlyList<byte[]>? found) ? found : [])];
    }

    /// <summary>Gets whether apps of <paramref name="source"/> install for one user without an administrator.</summary>
    public bool IsTrusted(AppPackageSource source) => GetTrust(source) != AppSourceTrust.Untrusted;

    /// <summary>
    /// Gets the trust level of a source. An app of a catalog has the catalog's level
    /// (<see cref="GetCatalogTrust"/>). A GitHub repository is <see cref="AppSourceTrust.Trusted"/> when its owner
    /// or the repository itself is in <c>[trusted]</c>. A repository is never
    /// <see cref="AppSourceTrust.Verified"/> by itself: that level belongs to the apps a Store installs
    /// from the official catalog (ADR 0188).
    /// </summary>
    public AppSourceTrust GetTrust(AppPackageSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Catalog is { } catalog)
        {
            // ADR 0212 §1: an app of a catalog takes the catalog's level (Verified for the official one).
            return GetCatalogTrust(catalog);
        }

        return TrustedKeys.Contains(source.TrustKey) || TrustedKeys.Contains(source.RepositoryTrustKey)
            ? AppSourceTrust.Trusted
            : AppSourceTrust.Untrusted;
    }

    /// <summary>Gets the trust level of a catalog named in <c>[catalogs]</c>.</summary>
    public AppSourceTrust GetCatalogTrust(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!TrustedKeys.Contains(AppSourcesConfigDocument.CatalogTrustPrefix + name))
        {
            return AppSourceTrust.Untrusted;
        }

        return string.Equals(name, OfficialCatalogName, StringComparison.OrdinalIgnoreCase)
            ? AppSourceTrust.Verified
            : AppSourceTrust.Trusted;
    }
}
