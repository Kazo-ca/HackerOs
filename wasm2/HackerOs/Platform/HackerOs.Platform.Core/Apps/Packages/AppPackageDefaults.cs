using System.Globalization;
using System.Reflection;

namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>
/// The app sources and the block list a host is built with (ADR 0189 §1 and §5): written to
/// <c>/etc/hackeros/app-sources.config</c> and <c>/etc/hackeros/app-policy.config</c> when those
/// files do not exist, and always trusted as "built in".
/// </summary>
/// <remarks>
/// A host sets them with MSBuild properties (<c>build/HackerOs.Apps.targets</c>), which become
/// <see cref="AssemblyMetadataAttribute"/>s of the host assembly read by <see cref="FromAssembly"/>:
/// <list type="table">
/// <item><term><c>HackerOsAppCatalogs</c></term><description><c>name=https://…/catalog.json</c>, comma separated.</description></item>
/// <item><term><c>HackerOsTrustedAppSources</c></term><description><c>catalog:official,github.com/Kazo-ca</c>.</description></item>
/// <item><term><c>HackerOsAppUpdateIntervalHours</c></term><description><c>6</c>.</description></item>
/// <item><term><c>HackerOsBlockedApps</c>, <c>HackerOsBlockedAppCategories</c>, <c>HackerOsBlockedAppCapabilities</c></term><description>empty by default.</description></item>
/// </list>
/// A property left unset keeps <see cref="Standard"/>'s value; an invalid item is skipped.
/// </remarks>
/// <param name="Catalogs">Catalogs by name, in file order.</param>
/// <param name="TrustedKeys">Trusted sources, in file order.</param>
/// <param name="UpdateIntervalHours">Hours between two update checks.</param>
/// <param name="BlockedApps">App IDs blocked for a personal install.</param>
/// <param name="BlockedCategories">Categories blocked for a personal install.</param>
/// <param name="BlockedCapabilities">Capabilities whose apps are blocked for a personal install.</param>
public sealed record AppPackageDefaults(
    IReadOnlyList<KeyValuePair<string, Uri>> Catalogs,
    IReadOnlyList<string> TrustedKeys,
    int UpdateIntervalHours,
    IReadOnlyList<string> BlockedApps,
    IReadOnlyList<string> BlockedCategories,
    IReadOnlyList<string> BlockedCapabilities)
{
    /// <summary>Hours between two update checks by default (ADR 0189 §1).</summary>
    public const int DefaultUpdateIntervalHours = 6;

    /// <summary>Address of the official catalog (ADR 0188).</summary>
    public const string OfficialCatalogAddress = "https://raw.githubusercontent.com/Kazo-ca/HackerOs-AppMarketPlace/packages/catalog.json";

    /// <summary>Prefix of the assembly metadata keys.</summary>
    public const string MetadataPrefix = "HackerOs.AppPackages.";

    /// <summary>The defaults of ADR 0189: the official catalog and <c>github.com/Kazo-ca</c> trusted, nothing blocked.</summary>
    public static AppPackageDefaults Standard { get; } = new(
        [new(AppSourcesConfig.OfficialCatalogName, new Uri(OfficialCatalogAddress))],
        [AppSourcesConfigDocument.CatalogTrustPrefix + AppSourcesConfig.OfficialCatalogName, AppSourcesConfigDocument.DefaultTrustedKey],
        DefaultUpdateIntervalHours,
        [],
        [],
        []);

    /// <summary>Reads the defaults a host assembly was built with; <see cref="Standard"/> for anything it does not set.</summary>
    /// <param name="assembly">The host assembly, or <see langword="null"/> for <see cref="Standard"/>.</param>
    public static AppPackageDefaults FromAssembly(Assembly? assembly) =>
        assembly is null
            ? Standard
            : FromMetadata(assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Where(static attribute => attribute.Key.StartsWith(MetadataPrefix, StringComparison.Ordinal))
                .GroupBy(static attribute => attribute.Key[MetadataPrefix.Length..], StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.Last().Value ?? string.Empty, StringComparer.Ordinal));

    /// <summary>Reads the defaults from metadata values keyed without <see cref="MetadataPrefix"/>.</summary>
    /// <param name="values">
    /// <c>Catalogs</c>, <c>Trusted</c>, <c>UpdateIntervalHours</c>, <c>BlockedApps</c>,
    /// <c>BlockedCategories</c>, <c>BlockedCapabilities</c>; a missing key keeps <see cref="Standard"/>'s value.
    /// </param>
    public static AppPackageDefaults FromMetadata(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        AppPackageDefaults result = Standard;
        if (values.TryGetValue("Catalogs", out string? catalogs))
        {
            result = result with
            {
                Catalogs = [.. Split(catalogs)
                    .Select(static item => AppSourceSpecification.TryParse(item, out AppSourceSpecification? parsed) ? parsed : null)
                    .OfType<AppSourceSpecification>()
                    .Where(static parsed => parsed.CatalogAddress is not null)
                    .Select(static parsed => new KeyValuePair<string, Uri>(parsed.Name, parsed.CatalogAddress!))]
            };
        }

        if (values.TryGetValue("Trusted", out string? trusted))
        {
            result = result with { TrustedKeys = [.. Split(trusted).Where(AppSourcesConfigDocument.IsTrustKey)] };
        }

        if (values.TryGetValue("UpdateIntervalHours", out string? hours)
            && int.TryParse(hours, NumberStyles.None, CultureInfo.InvariantCulture, out int interval)
            && interval is > 0 and <= AppSourcesConfigDocument.MaximumIntervalHours)
        {
            result = result with { UpdateIntervalHours = interval };
        }

        if (values.TryGetValue("BlockedApps", out string? apps))
        {
            result = result with { BlockedApps = [.. Split(apps)] };
        }

        if (values.TryGetValue("BlockedCategories", out string? categories))
        {
            result = result with { BlockedCategories = [.. Split(categories)] };
        }

        if (values.TryGetValue("BlockedCapabilities", out string? capabilities))
        {
            result = result with { BlockedCapabilities = [.. Split(capabilities)] };
        }

        return result;
    }

    private static IEnumerable<string> Split(string value) =>
        value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase);
}
