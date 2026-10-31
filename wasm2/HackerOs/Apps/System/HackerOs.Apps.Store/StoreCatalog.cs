using System.Globalization;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Apps;

namespace HackerOs.Apps.Store;

/// <summary>Where an app of the Store stands for the session user.</summary>
internal enum StoreAppState
{
    /// <summary>Not installed, for the user nor for the system.</summary>
    NotInstalled,

    /// <summary>Installed and running in this session's catalog.</summary>
    Installed,

    /// <summary>Installed, and its source publishes a newer version.</summary>
    UpdateAvailable,

    /// <summary>Installed, updated or removed since the OS started: shows after a restart (ADR 0182, Q5 C).</summary>
    PendingRestart
}

/// <summary>One app of the Store: its catalog entry, and what is installed of it.</summary>
/// <param name="App">Its entry in a catalog, or <see langword="null"/> for an installed app no catalog lists any more.</param>
/// <param name="Installed">What is installed, or <see langword="null"/>.</param>
/// <param name="Update">A newer version of its source, or <see langword="null"/>.</param>
/// <param name="Removed">Whether it was removed in this session (still loaded until a restart).</param>
internal sealed record StoreEntry(HackCatalogApp? App, HackInstalledPackage? Installed, HackPackageUpdate? Update, bool Removed)
{
    /// <summary>Gets the app ID.</summary>
    public string AppId => App?.AppId ?? Installed!.AppId;

    /// <summary>Gets the name to show.</summary>
    public string Name => App?.Name ?? Installed!.AppId;

    /// <summary>Gets the state of its buttons.</summary>
    public StoreAppState State =>
        Removed || Installed is { Loaded: false } ? StoreAppState.PendingRestart
        : Installed is null ? StoreAppState.NotInstalled
        : Update is not null ? StoreAppState.UpdateAvailable
        : StoreAppState.Installed;
}

/// <summary>
/// What the Hack Store shows (ADR 0188 §2): the apps of every catalog merged with what is installed
/// and the available updates. Pure data, so the window's rules are tested without a browser.
/// </summary>
/// <remarks>
/// An app listed by several catalogs is shown once, from the first catalog in the OS's order (the
/// official one first, then by name: ADR 0212 §4), like <c>hackpkg install &lt;appId&gt;</c>; an
/// installed copy keeps the catalog it came from.
/// </remarks>
internal sealed class StoreCatalog
{
    /// <summary>An empty Store, before anything was read.</summary>
    public static StoreCatalog Empty { get; } = new([], [], [], new HashSet<string>(StringComparer.Ordinal));

    private readonly Dictionary<string, StoreEntry> _entries;

    /// <summary>Builds the Store.</summary>
    /// <param name="catalogs">The catalogs, in the OS's order.</param>
    /// <param name="installed">What is installed for the system and for the user.</param>
    /// <param name="updates">The available updates.</param>
    /// <param name="removed">The apps removed in this session.</param>
    public StoreCatalog(
        IReadOnlyList<HackAppCatalog> catalogs,
        IReadOnlyList<HackInstalledPackage> installed,
        IReadOnlyList<HackPackageUpdate> updates,
        IReadOnlySet<string> removed)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(removed);
        Catalogs = catalogs;
        // One copy per app (ADR 0208 §3): the system copy wins over a personal one.
        Dictionary<string, HackInstalledPackage> byId = installed
            .GroupBy(static package => package.AppId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.OrderByDescending(static package => package.SystemWide).First(), StringComparer.Ordinal);
        Dictionary<string, HackPackageUpdate> updateById = updates
            .GroupBy(static update => update.AppId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        _entries = new Dictionary<string, StoreEntry>(StringComparer.Ordinal);
        foreach (HackAppCatalog catalog in catalogs)
        {
            // Theme packs have their own tab (ADR 0213 §5): StoreThemePacks.
            foreach (HackCatalogApp app in catalog.Apps.Where(static item => item.Kind == HackCatalogItemKind.App))
            {
                byId.TryGetValue(app.AppId, out HackInstalledPackage? package);
                // The first catalog wins, except that an installed copy shows the page of the catalog it came from.
                bool installedFromHere = package is not null && package.Source.Equals(app.Source, StringComparison.OrdinalIgnoreCase);
                if (_entries.ContainsKey(app.AppId) && !installedFromHere)
                {
                    continue;
                }

                _entries[app.AppId] = new StoreEntry(
                    app, package, updateById.GetValueOrDefault(app.AppId), removed.Contains(app.AppId));
            }
        }

        foreach (HackInstalledPackage package in byId.Values.Where(package => !_entries.ContainsKey(package.AppId)))
        {
            _entries[package.AppId] = new StoreEntry(null, package, updateById.GetValueOrDefault(package.AppId), removed.Contains(package.AppId));
        }

        Categories = [.. catalogs
            .SelectMany(static catalog => catalog.Categories)
            .GroupBy(static category => category.Id, StringComparer.Ordinal)
            .Select(static group => group.First())];
    }

    /// <summary>Gets the catalogs as read, with their errors.</summary>
    public IReadOnlyList<HackAppCatalog> Catalogs { get; }

    /// <summary>Gets the categories of every catalog, once each.</summary>
    public IReadOnlyList<HackCatalogCategory> Categories { get; }

    /// <summary>Gets the catalogs that could not be read.</summary>
    public IEnumerable<HackAppCatalog> FailedCatalogs => Catalogs.Where(static catalog => catalog.Error is not null);

    /// <summary>Gets the apps a catalog lists, by name.</summary>
    public IReadOnlyList<StoreEntry> Available =>
        [.. _entries.Values.Where(static entry => entry.App is not null).OrderBy(static entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>Gets the installed apps (and those removed in this session), by name: the « Installed » tab.</summary>
    public IReadOnlyList<StoreEntry> Installed =>
        [.. _entries.Values.Where(static entry => entry.Installed is not null || entry.Removed).OrderBy(static entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>Gets the apps with an update.</summary>
    public IReadOnlyList<StoreEntry> Updatable => [.. Installed.Where(static entry => entry.State == StoreAppState.UpdateAvailable)];

    /// <summary>Gets whether a change waits for a restart of the OS.</summary>
    public bool HasPendingRestart => _entries.Values.Any(static entry => entry.State == StoreAppState.PendingRestart);

    /// <summary>Gets one app, or <see langword="null"/>.</summary>
    /// <param name="appId">The app.</param>
    public StoreEntry? Find(string appId) => _entries.GetValueOrDefault(appId);

    /// <summary>Gets the apps of a category, by name.</summary>
    /// <param name="categoryId">The category.</param>
    public IReadOnlyList<StoreEntry> InCategory(string categoryId) =>
        [.. Available.Where(entry => string.Equals(entry.App!.Category, categoryId, StringComparison.Ordinal))];

    /// <summary>
    /// The home page (ADR 0188 §2): each category that has apps, in the catalogs' order, then the
    /// apps whose category no catalog names.
    /// </summary>
    public IReadOnlyList<(HackCatalogCategory Category, IReadOnlyList<StoreEntry> Apps)> Home()
    {
        List<(HackCatalogCategory, IReadOnlyList<StoreEntry>)> sections = [];
        foreach (HackCatalogCategory category in Categories)
        {
            IReadOnlyList<StoreEntry> apps = InCategory(category.Id);
            if (apps.Count > 0)
            {
                sections.Add((category, apps));
            }
        }

        HashSet<string> named = [.. Categories.Select(static category => category.Id)];
        IReadOnlyList<StoreEntry> others = [.. Available.Where(entry => !named.Contains(entry.App!.Category))];
        if (others.Count > 0)
        {
            sections.Add((new HackCatalogCategory(OtherCategoryId, new Dictionary<string, string>()), others));
        }

        return sections;
    }

    /// <summary>ID of the section of apps whose category no catalog names.</summary>
    public const string OtherCategoryId = "";

    /// <summary>
    /// Searches the name, the short description (in <paramref name="culture"/>'s language and in
    /// English), the tags and the app ID (ADR 0188 §2), every word of the query in any of them,
    /// without case or accents.
    /// </summary>
    /// <param name="query">What the user typed.</param>
    /// <param name="culture">Language of the descriptions.</param>
    public IReadOnlyList<StoreEntry> Search(string? query, CultureInfo culture)
    {
        string[] words = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return Available;
        }

        return [.. Available.Where(entry =>
        {
            HackCatalogApp app = entry.App!;
            string haystack = Normalize(string.Join(' ', new[]
            {
                app.Name,
                app.AppId,
                app.GetShortDescription(culture),
                app.ShortDescriptions.GetValueOrDefault("en") ?? string.Empty,
            }.Concat(app.Tags)));
            return words.All(word => haystack.Contains(word, StringComparison.Ordinal));
        })];
    }

    /// <summary>Gets whether a capability needs the user's approval at first launch (ADR 0049).</summary>
    /// <param name="capability">The capability.</param>
    public static bool IsDangerous(string capability) => AppCapabilities.IsDangerous(capability);

    /// <summary>Formats a size the way the File Explorer does: bytes, KB, MB.</summary>
    /// <param name="bytes">The size.</param>
    /// <param name="culture">Number format.</param>
    public static string FormatSize(long bytes, CultureInfo culture) => bytes switch
    {
        < 1024 => string.Format(culture, "{0} B", bytes),
        < 1024 * 1024 => string.Format(culture, "{0:0.#} KB", bytes / 1024d),
        _ => string.Format(culture, "{0:0.#} MB", bytes / (1024d * 1024d))
    };

    /// <summary>Lower case, without accents, with every space as one space: the form searches compare.</summary>
    /// <param name="text">The text.</param>
    internal static string Normalize(string? text)
    {
        string decomposed = (text ?? string.Empty).Normalize(System.Text.NormalizationForm.FormD);
        System.Text.StringBuilder builder = new(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.IsWhiteSpace(c) ? ' ' : char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }
}
