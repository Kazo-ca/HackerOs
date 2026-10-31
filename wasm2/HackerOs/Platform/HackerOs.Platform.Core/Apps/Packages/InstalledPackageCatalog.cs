namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>
/// The apps installed from packages that joined the running catalog at boot (ADR 0182 §5, ADR 0189 §3),
/// with who may see each one: a system install is seen by every session, a personal install only by
/// the sessions of the users who installed it.
/// </summary>
/// <remarks>
/// The page runs one catalog for every session: the boot loads every user's personal apps and this
/// registry hides the others' from each session (<see cref="AppInstallationService"/> treats them as
/// uninstalled for that user, and the app list leaves them out).
/// </remarks>
public sealed class InstalledPackageCatalog
{
    private readonly Lock _gate = new();
    private IReadOnlyDictionary<string, InstalledApp> _apps = new Dictionary<string, InstalledApp>(StringComparer.Ordinal);

    /// <summary>Gets the installed apps of the running catalog, by app ID.</summary>
    public IReadOnlyDictionary<string, InstalledApp> Apps
    {
        get
        {
            lock (_gate)
            {
                return _apps;
            }
        }
    }

    /// <summary>Records the installed apps the catalog accepted at boot.</summary>
    /// <param name="apps">The accepted installed apps.</param>
    public void Apply(IEnumerable<InstalledApp> apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        Dictionary<string, InstalledApp> byId = apps.ToDictionary(static app => app.AppId, StringComparer.Ordinal);
        lock (_gate)
        {
            _apps = byId;
        }
    }

    /// <summary>Gets whether <paramref name="appId"/> came from a package (system or personal).</summary>
    public bool Contains(string appId) => Apps.ContainsKey(appId);

    /// <summary>Gets whether the session of the user whose home is <paramref name="homePath"/> may see the app.</summary>
    /// <param name="appId">Any catalog app ID; apps not installed from a package are always visible.</param>
    /// <param name="homePath">The session user's home, or <see langword="null"/> without a session.</param>
    public bool IsVisibleTo(string appId, string? homePath) =>
        !Apps.TryGetValue(appId, out InstalledApp? app)
        || app.IsSystemWide
        || (homePath is not null && app.OwnerHomes.Contains(homePath.TrimEnd('/')));

    /// <summary>Gets the personal apps of other users, hidden from the session of <paramref name="homePath"/>.</summary>
    /// <param name="homePath">The session user's home, or <see langword="null"/> without a session.</param>
    public IReadOnlySet<string> GetHiddenFor(string? homePath) =>
        Apps.Keys.Where(appId => !IsVisibleTo(appId, homePath)).ToHashSet(StringComparer.Ordinal);
}
