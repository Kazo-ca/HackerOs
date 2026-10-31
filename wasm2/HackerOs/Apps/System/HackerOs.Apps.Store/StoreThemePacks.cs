using System.Globalization;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Apps;

namespace HackerOs.Apps.Store;

/// <summary>Where a theme pack of the Store stands for the session user.</summary>
internal enum StoreThemePackState
{
    /// <summary>Not installed, for the user nor for the system.</summary>
    NotInstalled,

    /// <summary>Installed: its parts are offered in Settings.</summary>
    Installed,

    /// <summary>Installed, and its catalog publishes a newer version.</summary>
    UpdateAvailable
}

/// <summary>One theme pack of the « Themes » tab (ADR 0213 §5): its catalog entry and its installed copy.</summary>
/// <param name="Pack">Its entry in a catalog, or <see langword="null"/> for an installed pack no catalog lists any more.</param>
/// <param name="Installed">The installed copy, or <see langword="null"/>.</param>
internal sealed record StoreThemePackEntry(HackCatalogApp? Pack, HackInstalledThemePack? Installed)
{
    /// <summary>Gets the pack id.</summary>
    public string PackId => Pack?.AppId ?? Installed!.PackId;

    /// <summary>Gets the name to show.</summary>
    public string Name => Pack?.Name ?? Installed!.Name;

    /// <summary>Gets the newer version of its catalog, or <see langword="null"/> (ADR 0213 §6: no badge, the tab shows it).</summary>
    public string? AvailableUpdate =>
        Pack is not null
        && Installed is not null
        && SemanticVersion.TryParse(Pack.LatestVersion, out SemanticVersion latest)
        && SemanticVersion.TryParse(Installed.Version, out SemanticVersion installed)
        && latest.CompareTo(installed) > 0
            ? Pack.LatestVersion
            : null;

    /// <summary>Gets the state of its buttons.</summary>
    public StoreThemePackState State =>
        Installed is null ? StoreThemePackState.NotInstalled
        : AvailableUpdate is not null ? StoreThemePackState.UpdateAvailable
        : StoreThemePackState.Installed;
}

/// <summary>
/// The theme packs the Store shows (ADR 0213 §5): the <c>kind: "theme-pack"</c> entries of every
/// catalog, the first catalog in the OS's order winning as for apps, merged with the installed packs.
/// Pure data, so the tab's rules are tested without a browser.
/// </summary>
internal sealed class StoreThemePacks
{
    /// <summary>An empty tab, before anything was read.</summary>
    public static StoreThemePacks Empty { get; } = new([], []);

    /// <summary>Builds the tab.</summary>
    /// <param name="catalogs">The catalogs, in the OS's order.</param>
    /// <param name="installed">The packs installed for the system and for the user.</param>
    public StoreThemePacks(IReadOnlyList<HackAppCatalog> catalogs, IReadOnlyList<HackInstalledThemePack> installed)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(installed);
        Dictionary<string, HackInstalledThemePack> byId = installed
            .GroupBy(static pack => pack.PackId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.OrderByDescending(static pack => pack.SystemWide).First(), StringComparer.Ordinal);
        Dictionary<string, StoreThemePackEntry> entries = new(StringComparer.Ordinal);
        foreach (HackCatalogApp pack in catalogs.SelectMany(static catalog => catalog.Apps).Where(static item => item.Kind == HackCatalogItemKind.ThemePack))
        {
            byId.TryGetValue(pack.AppId, out HackInstalledThemePack? copy);
            bool installedFromHere = copy is not null && copy.Source.StartsWith(pack.Source + "@", StringComparison.OrdinalIgnoreCase);
            if (!entries.ContainsKey(pack.AppId) || installedFromHere)
            {
                entries[pack.AppId] = new StoreThemePackEntry(pack, copy);
            }
        }

        foreach (HackInstalledThemePack copy in byId.Values.Where(copy => !entries.ContainsKey(copy.PackId)))
        {
            entries[copy.PackId] = new StoreThemePackEntry(null, copy);
        }

        All = [.. entries.Values.OrderBy(static entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Gets every pack, by name.</summary>
    public IReadOnlyList<StoreThemePackEntry> All { get; }

    /// <summary>Gets one pack, or <see langword="null"/>.</summary>
    /// <param name="packId">The pack.</param>
    public StoreThemePackEntry? Find(string packId) => All.FirstOrDefault(entry => entry.PackId == packId);

    /// <summary>Searches the name, the short description, the tags and the id, every word in any of them, like the apps.</summary>
    /// <param name="query">What the user typed.</param>
    /// <param name="culture">Language of the descriptions.</param>
    public IReadOnlyList<StoreThemePackEntry> Search(string? query, CultureInfo culture)
    {
        string[] words = StoreCatalog.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0
            ? All
            : [.. All.Where(entry =>
            {
                string haystack = StoreCatalog.Normalize(string.Join(' ', new[]
                {
                    entry.Name,
                    entry.PackId,
                    entry.Pack?.GetShortDescription(culture) ?? string.Empty,
                    entry.Pack?.ShortDescriptions.GetValueOrDefault("en") ?? string.Empty,
                }.Concat(entry.Pack?.Tags ?? [])));
                return words.All(word => haystack.Contains(word, StringComparison.Ordinal));
            })];
    }
}
