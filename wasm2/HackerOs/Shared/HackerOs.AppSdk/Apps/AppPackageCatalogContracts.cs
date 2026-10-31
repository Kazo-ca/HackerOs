using System.Globalization;

namespace HackerOs.AppSdk.Apps;

/// <summary>A category of a catalog (ADR 0188 §4), with its label per language.</summary>
/// <param name="Id">Category ID, such as <c>productivity</c>.</param>
/// <param name="Labels">Label per language code (<c>en</c>, <c>fr</c>, …).</param>
public sealed record HackCatalogCategory(string Id, IReadOnlyDictionary<string, string> Labels)
{
    /// <summary>Gets the label in <paramref name="culture"/>'s language, else English, else the ID.</summary>
    /// <param name="culture">The language wanted.</param>
    public string GetLabel(CultureInfo culture) => HackCatalogText.Pick(Labels, culture) ?? Id;
}

/// <summary>One published version of an app of a catalog (ADR 0188 §6).</summary>
/// <param name="Version">The version.</param>
/// <param name="SizeBytes">Size of its package.</param>
/// <param name="Capabilities">Capabilities it declares; dangerous ones are asked at its first launch (ADR 0049).</param>
public sealed record HackCatalogAppVersion(string Version, long SizeBytes, IReadOnlyList<string> Capabilities)
{
    /// <summary>Gets when the catalog published it, if it says.</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>Gets the oldest SDK it runs on, if it says.</summary>
    public string? MinimumSdkVersion { get; init; }

    /// <summary>Gets the address of its 256×256 PNG icon, if any.</summary>
    public string? IconUri { get; init; }

    /// <summary>Gets the addresses of its screenshots.</summary>
    public IReadOnlyList<string> ScreenshotUris { get; init; } = [];

    /// <summary>Gets the languages its long description is written in (read it with <see cref="HackAppPackages.GetCatalogDescriptionAsync"/>).</summary>
    public IReadOnlyList<string> DescriptionLanguages { get; init; } = [];
}

/// <summary>An app of a catalog, as the Hack Store shows it (ADR 0188 §2).</summary>
/// <param name="Catalog">Name of its catalog in <c>[catalogs]</c>.</param>
/// <param name="AppId">App identifier.</param>
/// <param name="Name">Its name.</param>
/// <param name="Category">Category ID.</param>
/// <param name="Tags">Free tags.</param>
/// <param name="ShortDescriptions">Short description per language.</param>
/// <param name="License">SPDX license expression.</param>
/// <param name="Repository">Its source code repository.</param>
/// <param name="LatestVersion">The version installed by default.</param>
/// <param name="Versions">Published versions, newest first.</param>
/// <param name="Trust">Trust level of its catalog (ADR 0189 §2).</param>
public sealed record HackCatalogApp(
    string Catalog,
    string AppId,
    string Name,
    string Category,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> ShortDescriptions,
    string License,
    string Repository,
    string LatestVersion,
    IReadOnlyList<HackCatalogAppVersion> Versions,
    HackPackageTrust Trust)
{
    /// <summary>Gets the source to pass to <see cref="HackAppPackages.InstallAsync"/>: <c>catalog:&lt;catalog&gt;/&lt;appId&gt;</c>.</summary>
    public string Source => $"catalog:{Catalog}/{AppId}";

    /// <summary>Gets the version <see cref="LatestVersion"/> names.</summary>
    public HackCatalogAppVersion Latest => Versions.FirstOrDefault(version => version.Version == LatestVersion) ?? Versions[0];

    /// <summary>Gets the short description in <paramref name="culture"/>'s language, else English.</summary>
    /// <param name="culture">The language wanted.</param>
    public string GetShortDescription(CultureInfo culture) => HackCatalogText.Pick(ShortDescriptions, culture) ?? string.Empty;
}

/// <summary>A catalog of <c>[catalogs]</c> and what it lists (ADR 0188).</summary>
/// <param name="Name">Catalog name.</param>
/// <param name="Trust">Its trust level: <see cref="HackPackageTrust.Verified"/> for the official catalog.</param>
/// <param name="Categories">Its categories.</param>
/// <param name="Apps">Its apps, in app ID order.</param>
public sealed record HackAppCatalog(
    string Name, HackPackageTrust Trust, IReadOnlyList<HackCatalogCategory> Categories, IReadOnlyList<HackCatalogApp> Apps)
{
    /// <summary>Gets why it could not be read (offline, invalid file), or <see langword="null"/>; it then lists nothing.</summary>
    public string? Error { get; init; }
}

/// <summary>A newer version published by the source of an installed app (ADR 0188 §3).</summary>
/// <param name="AppId">The installed app.</param>
/// <param name="Source">Its source, such as <c>catalog:official/org.example.notes</c>.</param>
/// <param name="InstalledVersion">The installed version.</param>
/// <param name="AvailableVersion">The newer version.</param>
/// <param name="SystemWide">Whether the installed copy is for every user (its update then asks an administrator).</param>
public sealed record HackPackageUpdate(string AppId, string Source, string InstalledVersion, string AvailableVersion, bool SystemWide);

/// <summary>Picks a text in a language map: the exact culture, its language, then English.</summary>
internal static class HackCatalogText
{
    internal static string? Pick(IReadOnlyDictionary<string, string> texts, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(culture);
        foreach (string key in new[] { culture.Name, culture.TwoLetterISOLanguageName, "en" })
        {
            if (key.Length > 0 && texts.TryGetValue(key, out string? text))
            {
                return text;
            }
        }

        return texts.Values.FirstOrDefault();
    }
}
