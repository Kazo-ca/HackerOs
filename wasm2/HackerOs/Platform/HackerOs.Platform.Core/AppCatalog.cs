using System.Collections.Frozen;
using System.Collections.ObjectModel;
using HackerOs.App.Abstractions;

namespace HackerOs.Platform.Core;

/// <summary>
/// Contains validated app manifests in deterministic dependency order.
/// </summary>
/// <remarks>
/// A built catalog never changes by itself. The OS's running catalog changes only when apps installed
/// from packages join or leave it (<see cref="ReplaceContents"/>, ADR 0214 §1): the whole contents are
/// swapped at once and <see cref="Changed"/> is raised.
/// </remarks>
public sealed class AppCatalog
{
    private CatalogState _state;

    private AppCatalog(
        IReadOnlyDictionary<string, AppManifest> manifests,
        IReadOnlyList<string> activationOrder,
        IReadOnlySet<string> systemAppIds,
        IReadOnlyList<AppCatalogRejection> rejected,
        IReadOnlyDictionary<string, string> commandOwners,
        IReadOnlyDictionary<string, IReadOnlyList<AppComponentProvider>> componentProviders,
        IReadOnlyList<AppCatalogError> warnings)
    {
        _state = new CatalogState(
            manifests,
            activationOrder,
            activationOrder.Reverse().ToArray(),
            systemAppIds,
            rejected,
            commandOwners,
            componentProviders,
            warnings,
            FrozenDictionary<string, IReadOnlyDictionary<string, AppManifestText>>.Empty);
    }

    /// <summary>
    /// Gets how many times <see cref="ReplaceContents"/> changed this catalog since the OS started
    /// (ADR 0214 §1): <c>0</c> for the catalog built at boot.
    /// </summary>
    public long Revision { get; private set; }

    /// <summary>
    /// Raised after <see cref="ReplaceContents"/> changed the apps, once the new contents are visible
    /// (ADR 0214 §1). Code that keeps a copy of anything read from the catalog refreshes it here.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Replaces every member of this catalog by those of <paramref name="next"/> at once, keeping this
    /// instance, then raises <see cref="Changed"/> (ADR 0214 §1): how apps installed, updated or removed
    /// from packages join or leave the running OS without a restart, as Linux desktops rebuild their
    /// application database after <c>apt install</c>.
    /// </summary>
    /// <param name="next">A catalog built with the same host input and the new installed apps.</param>
    /// <remarks>
    /// Only the host's installed-apps holder calls it. A reader sees either the old or the new
    /// contents, never a mix of both, as long as it reads one member per decision.
    /// </remarks>
    public void ReplaceContents(AppCatalog next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (ReferenceEquals(next, this))
        {
            return;
        }

        Volatile.Write(ref _state, next._state);
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Creates another instance with the same contents, for a host to hand out as its running catalog
    /// and later <see cref="ReplaceContents"/> without touching this one (ADR 0214 §1).
    /// </summary>
    /// <returns>A new catalog, <see cref="Revision"/> <c>0</c>, with no <see cref="Changed"/> subscriber.</returns>
    public AppCatalog CreateLiveCopy() => new(State);

    private AppCatalog(CatalogState state)
    {
        _state = state;
    }

    private CatalogState State => Volatile.Read(ref _state);

    /// <summary>Gets manifests keyed by immutable app ID.</summary>
    public IReadOnlyDictionary<string, AppManifest> Manifests => State.Manifests;

    /// <summary>Gets app IDs with dependencies ordered before dependents.</summary>
    public IReadOnlyList<string> ActivationOrder => State.ActivationOrder;

    /// <summary>Gets app IDs with dependents ordered before dependencies.</summary>
    public IReadOnlyList<string> DeactivationOrder => State.DeactivationOrder;

    /// <summary>
    /// Gets the IDs of the apps the host declared system apps (ADR 0048 §1). A system app cannot be
    /// uninstalled and receives its dangerous capabilities without approval (ADR 0049).
    /// </summary>
    /// <remarks>
    /// The set only comes from trusted host input: the <c>isSystemApp</c> flags of the host's
    /// <c>BuiltInApplications.json</c>, written by the build from the <c>IsSystemApp</c> metadata of
    /// its <c>ProjectReference</c>. Neither <see cref="AppManifest"/> nor <c>AppDefinition</c> can
    /// carry the flag, so an app can never declare itself a system app.
    /// </remarks>
    public IReadOnlySet<string> SystemAppIds => State.SystemAppIds;

    /// <summary>
    /// Gets the apps the host shipped but this OS refuses to load (ADR 0098 §3), ordered by id: an
    /// SDK range that does not contain <see cref="HackerOsSdk.Version"/>
    /// (<c>catalog.sdk.incompatible</c>), or a required dependency refused for that reason
    /// (<c>catalog.dependency.unavailable</c>). They are not in <see cref="Manifests"/>, so nothing
    /// can launch them; Settings lists them as incompatible with the range they asked for.
    /// </summary>
    public IReadOnlyList<AppCatalogRejection> Rejected => State.Rejected;

    /// <summary>
    /// Gets the app that owns each terminal command name and alias (case-insensitive keys), the
    /// shell's <c>PATH</c> (ADR 0098 §4). When two apps declare the same name, a system app keeps
    /// it, otherwise the first in <see cref="ActivationOrder"/>; the other app stays launchable but
    /// its command under that name is not registered, and <see cref="Warnings"/> says so.
    /// </summary>
    public IReadOnlyDictionary<string, string> CommandOwners => State.CommandOwners;

    /// <summary>
    /// Gets the providers of each embeddable component contract (ADR 0063, ADR 0148 §3), read from
    /// the manifests when the catalog is built, as Windows reads its registry at boot: the OS knows
    /// every provider before any of their assemblies is loaded. Each list is in the order the
    /// providers are tried: system apps first, then by app id. Any window app may register any
    /// contract but the platform's own (ADR 0156 §1, which replaced the system-app reservation of
    /// ADR 0148 §3.3).
    /// </summary>
    /// <remarks>
    /// Installed state and the administrator's choice of provider (<c>/etc/hackeros/components.config</c>,
    /// ADR 0156 §2) are not applied here: the component host applies both when it resolves.
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyList<AppComponentProvider>> ComponentProviders => State.ComponentProviders;

    /// <summary>Gets the providers of <paramref name="contract"/> in the order they are tried (see <see cref="ComponentProviders"/>).</summary>
    /// <param name="contract">Contract name (ordinal comparison).</param>
    /// <returns>The providers, empty when no app registers the contract.</returns>
    public IReadOnlyList<AppComponentProvider> GetComponentProviders(string contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return ComponentProviders.TryGetValue(contract, out IReadOnlyList<AppComponentProvider>? providers) ? providers : [];
    }

    /// <summary>
    /// Gets the problems that did not stop the catalog, in deterministic order: one
    /// <c>catalog.command.duplicate</c> per command name lost by an app. The boot writes them, with
    /// <see cref="Rejected"/>, to <c>/var/log/syslog</c>.
    /// </summary>
    public IReadOnlyList<AppCatalogError> Warnings => State.Warnings;

    /// <summary>
    /// Gets the app whose command answers to <paramref name="commandName"/> (name or alias,
    /// case-insensitive), or <see langword="null"/> when none does.
    /// </summary>
    /// <param name="commandName">The name typed in the shell.</param>
    /// <returns>The owning app id.</returns>
    public string? FindCommandOwner(string commandName)
    {
        ArgumentNullException.ThrowIfNull(commandName);
        return CommandOwners.TryGetValue(commandName, out string? owner) ? owner : null;
    }

    /// <summary>Gets whether <paramref name="appId"/> is a system app of this catalog.</summary>
    /// <param name="appId">Application identifier (ordinal comparison).</param>
    /// <returns><see langword="true"/> when the host declared the app a system app.</returns>
    public bool IsSystemApp(string appId)
    {
        ArgumentNullException.ThrowIfNull(appId);
        return SystemAppIds.Contains(appId);
    }

    /// <summary>Builds a catalog without loading or executing app assemblies.</summary>
    /// <param name="manifests">Candidate manifests selected by the build or package policy.</param>
    /// <returns>A catalog with no system app, or every deterministic validation error.</returns>
    public static AppCatalogBuildResult Build(IEnumerable<AppManifest> manifests) => Build(manifests, []);

    /// <summary>
    /// Builds a catalog for the SDK version of this process (<see cref="HackerOsSdk.SemanticVersion"/>).
    /// See <see cref="Build(IEnumerable{AppManifest}, IEnumerable{string}, SemanticVersion)"/>.
    /// </summary>
    /// <param name="manifests">Candidate manifests selected by the build or package policy.</param>
    /// <param name="systemAppIds">IDs the trusted host declared system apps.</param>
    /// <returns>A catalog or every deterministic validation error.</returns>
    public static AppCatalogBuildResult Build(IEnumerable<AppManifest> manifests, IEnumerable<string> systemAppIds) =>
        Build(manifests, systemAppIds, HackerOsSdk.SemanticVersion);

    /// <summary>
    /// Builds a host catalog from its aggregated <c>BuiltInApplications.json</c> entries: the
    /// manifests, plus the system-app flags the host wrote for them (ADR 0048 §1).
    /// </summary>
    /// <param name="applications">Built-in applications read from the host resource.</param>
    /// <returns>A catalog or every deterministic validation error.</returns>
    public static AppCatalogBuildResult BuildFromBuiltInApplications(IEnumerable<BuiltInApplication> applications)
    {
        ArgumentNullException.ThrowIfNull(applications);
        BuiltInApplication[] items = applications.ToArray();
        AppCatalogBuildResult result = Build(
            items.Select(item => item.Manifest),
            items.Where(item => item.IsSystemApp).Select(item => item.Manifest.Id));
        if (result.Catalog is { } catalog)
        {
            // ADR 0104 section 4: the translated texts the host build read from each app's localization files.
            catalog.Localized = items
                .Where(static item => item.Localized is { Count: > 0 })
                .ToFrozenDictionary(static item => item.Manifest.Id, static item => item.Localized!, StringComparer.Ordinal);
        }

        return result;
    }

    /// <summary>
    /// Gets the translated name, description and usage of each app, by app id then culture
    /// (ADR 0104, section 4). Apps only in English are absent.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, AppManifestText>> Localized
    {
        get => State.Localized;
        private set => _state = _state with { Localized = value };
    }

    /// <summary>
    /// Returns <paramref name="manifest"/> with its name, description and usage in
    /// <paramref name="culture"/> when this catalog has them (ADR 0104, section 4), for display
    /// only: ids, command names and everything else stay as they are.
    /// </summary>
    /// <param name="manifest">A manifest of this catalog (any other is returned unchanged).</param>
    /// <param name="culture">The language of the surface showing it.</param>
    public AppManifest Localize(AppManifest manifest, System.Globalization.CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(culture);
        return AppManifestLocalization.Apply(manifest, Localized.GetValueOrDefault(manifest.Id), culture);
    }

    /// <summary>Builds a catalog without loading or executing app assemblies.</summary>
    /// <param name="manifests">Candidate manifests selected by the build or package policy.</param>
    /// <param name="systemAppIds">
    /// IDs the trusted host declared system apps. Each must name a manifest of the catalog
    /// (<c>catalog.system-app.unknown</c> otherwise), so a typo cannot silently leave an app
    /// uninstallable or unprotected.
    /// </param>
    /// <param name="sdkVersion">
    /// The App SDK version the apps must support (ADR 0098 §3). An app whose
    /// <c>sdkCompatibility</c> range does not contain it, and every app that requires it, is moved
    /// to <see cref="Rejected"/> instead of failing the whole catalog: one app built for another SDK
    /// must not keep the OS from booting.
    /// </param>
    /// <returns>A catalog or every deterministic validation error.</returns>
    public static AppCatalogBuildResult Build(IEnumerable<AppManifest> manifests, IEnumerable<string> systemAppIds, SemanticVersion sdkVersion)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(systemAppIds);

        AppManifest[] candidates = manifests.ToArray();
        List<AppCatalogError> errors = [];
        Dictionary<string, AppManifest> byId = new(StringComparer.Ordinal);

        foreach (AppManifest manifest in candidates.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            ManifestValidationResult validation = AppManifestValidator.Validate(manifest);
            foreach (ManifestValidationError error in validation.Errors)
            {
                errors.Add(new AppCatalogError(
                    "catalog.manifest.invalid",
                    manifest.Id,
                    $"{error.Path}: {error.Message}"));
            }

            if (!byId.TryAdd(manifest.Id, manifest))
            {
                errors.Add(new AppCatalogError(
                    "catalog.app-id.duplicate",
                    manifest.Id,
                    $"App ID '{manifest.Id}' is registered more than once."));
            }
        }

        ValidateWebHostNames(byId.Values, errors);

        HashSet<string> systemIds = new(StringComparer.Ordinal);
        foreach (string systemAppId in systemAppIds.Order(StringComparer.Ordinal))
        {
            if (systemIds.Add(systemAppId) && !byId.ContainsKey(systemAppId))
            {
                errors.Add(new AppCatalogError(
                    "catalog.system-app.unknown",
                    systemAppId,
                    $"System app '{systemAppId}' is not part of the catalog."));
            }
        }

        if (errors.Count > 0)
        {
            return new AppCatalogBuildResult(null, errors);
        }

        IReadOnlyList<AppCatalogRejection> rejected = RejectIncompatible(byId, sdkVersion);

        ValidateDependencies(byId, errors);
        if (errors.Count > 0)
        {
            return new AppCatalogBuildResult(null, errors);
        }

        IReadOnlyList<string>? activationOrder = CreateActivationOrder(byId, errors);
        if (activationOrder is null)
        {
            return new AppCatalogBuildResult(null, errors);
        }

        ReadOnlyDictionary<string, AppManifest> readOnlyManifests =
            new(new Dictionary<string, AppManifest>(byId, StringComparer.Ordinal));
        List<AppCatalogError> warnings = [];
        IReadOnlyDictionary<string, string> commandOwners = AssignCommands(byId, activationOrder, systemIds, warnings);
        IReadOnlyDictionary<string, IReadOnlyList<AppComponentProvider>> componentProviders =
            RegisterComponents(byId, systemIds);
        return new AppCatalogBuildResult(
            new AppCatalog(
                readOnlyManifests,
                activationOrder,
                systemIds.ToFrozenSet(StringComparer.Ordinal),
                rejected,
                commandOwners,
                componentProviders,
                warnings),
            []);
    }

    /// <summary>
    /// ADR 0098 §4, at load: gives each terminal command name and alias to one app. System apps
    /// choose first, then the others, each group in activation order; a name already taken produces
    /// one <c>catalog.command.duplicate</c> warning for the app that loses it. (The host build
    /// already refuses duplicates among its own apps with <c>HOSAPP0022</c>; this covers apps that
    /// reach the catalog another way.)
    /// </summary>
    private static IReadOnlyDictionary<string, string> AssignCommands(
        IReadOnlyDictionary<string, AppManifest> manifests,
        IReadOnlyList<string> activationOrder,
        IReadOnlySet<string> systemIds,
        List<AppCatalogError> warnings)
    {
        Dictionary<string, string> owners = new(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> precedence = activationOrder.Where(systemIds.Contains)
            .Concat(activationOrder.Where(id => !systemIds.Contains(id)));
        foreach (string appId in precedence)
        {
            if (manifests[appId].Terminal is not { } terminal)
            {
                continue;
            }

            foreach (string name in terminal.Aliases.Prepend(terminal.Name).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (owners.TryAdd(name, appId))
                {
                    continue;
                }

                warnings.Add(new AppCatalogError(
                    "catalog.command.duplicate",
                    appId,
                    $"Command '{name}' of '{appId}' is not registered: '{owners[name]}' already provides it."));
            }
        }

        return new ReadOnlyDictionary<string, string>(owners);
    }

    /// <summary>
    /// ADR 0148 §3: builds the component registry from the manifests, system apps first, then by
    /// app id. Platform-only contracts never get here: manifest validation refuses them.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<AppComponentProvider>> RegisterComponents(
        IReadOnlyDictionary<string, AppManifest> manifests,
        IReadOnlySet<string> systemIds)
    {
        Dictionary<string, List<AppComponentProvider>> providers = new(StringComparer.Ordinal);
        IEnumerable<AppManifest> precedence = manifests.Values
            .OrderBy(manifest => systemIds.Contains(manifest.Id) ? 0 : 1)
            .ThenBy(manifest => manifest.Id, StringComparer.Ordinal);
        foreach (AppManifest manifest in precedence)
        {
            foreach (AppComponentManifest component in manifest.Components ?? [])
            {
                if (!providers.TryGetValue(component.Contract, out List<AppComponentProvider>? list))
                {
                    providers[component.Contract] = list = [];
                }

                list.Add(new AppComponentProvider(manifest.Id, component));
            }
        }

        return providers.ToFrozenDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<AppComponentProvider>)entry.Value.AsReadOnly(),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// ADR 0098 §3: removes from <paramref name="byId"/> every app whose SDK range does not contain
    /// <paramref name="sdkVersion"/>, then, until nothing changes, every app with a required
    /// dependency so removed. Runs after manifest validation, so every range parses.
    /// </summary>
    private static IReadOnlyList<AppCatalogRejection> RejectIncompatible(
        Dictionary<string, AppManifest> byId,
        SemanticVersion sdkVersion)
    {
        List<AppCatalogRejection> rejected = [];
        foreach (AppManifest manifest in byId.Values.OrderBy(static item => item.Id, StringComparer.Ordinal).ToArray())
        {
            if (HackerOsSdk.IsCompatible(manifest.SdkCompatibility, sdkVersion))
            {
                continue;
            }

            byId.Remove(manifest.Id);
            string range = manifest.SdkCompatibility.MaximumVersion is { } maximum
                ? $"{manifest.SdkCompatibility.MinimumVersion} to {maximum}"
                : $"{manifest.SdkCompatibility.MinimumVersion} or later in major version {SemanticVersion.Parse(manifest.SdkCompatibility.MinimumVersion).Major}";
            rejected.Add(new AppCatalogRejection(
                "catalog.sdk.incompatible",
                manifest,
                $"'{manifest.Id}' needs App SDK {range}; this system runs {sdkVersion}."));
        }

        bool changed = rejected.Count > 0;
        while (changed)
        {
            changed = false;
            foreach (AppManifest manifest in byId.Values.OrderBy(static item => item.Id, StringComparer.Ordinal).ToArray())
            {
                AppDependencyManifest? missing = manifest.Dependencies.FirstOrDefault(dependency =>
                    !dependency.Optional && rejected.Any(item => item.AppId == dependency.AppId));
                if (missing is null)
                {
                    continue;
                }

                byId.Remove(manifest.Id);
                rejected.Add(new AppCatalogRejection(
                    "catalog.dependency.unavailable",
                    manifest,
                    $"'{manifest.Id}' requires '{missing.AppId}', which this system cannot load."));
                changed = true;
            }
        }

        return [.. rejected.OrderBy(static item => item.AppId, StringComparer.Ordinal)];
    }

    /// <summary>
    /// ADR 0073, section 1: the resolver maps a host name to one web app, so two apps may not declare
    /// the same name (<c>catalog.web.url-conflict</c>), whatever the port or path.
    /// </summary>
    private static void ValidateWebHostNames(IEnumerable<AppManifest> manifests, List<AppCatalogError> errors)
    {
        Dictionary<string, string> owners = new(StringComparer.OrdinalIgnoreCase);
        foreach (AppManifest manifest in manifests.Where(static item => item.Web is not null).OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            HashSet<string> ownHosts = new(StringComparer.OrdinalIgnoreCase);
            foreach (string url in manifest.Web!.Urls)
            {
                if (!WebAppUrls.TryParse(url, allowWildcardHost: false, out WebBinding binding, out _) || !ownHosts.Add(binding.Host))
                {
                    continue;
                }

                if (!owners.TryAdd(binding.Host, manifest.Id))
                {
                    errors.Add(new AppCatalogError(
                        "catalog.web.url-conflict",
                        manifest.Id,
                        $"Host name '{binding.Host}' is already declared by '{owners[binding.Host]}'."));
                }
            }
        }
    }

    private static void ValidateDependencies(
        IReadOnlyDictionary<string, AppManifest> manifests,
        List<AppCatalogError> errors)
    {
        foreach (AppManifest manifest in manifests.Values.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            foreach (AppDependencyManifest dependency in manifest.Dependencies.OrderBy(item => item.AppId, StringComparer.Ordinal))
            {
                if (!manifests.TryGetValue(dependency.AppId, out AppManifest? installed))
                {
                    if (!dependency.Optional)
                    {
                        errors.Add(new AppCatalogError(
                            "catalog.dependency.missing",
                            manifest.Id,
                            $"Required dependency '{dependency.AppId}' is missing."));
                    }

                    continue;
                }

                SemanticVersion installedVersion = SemanticVersion.Parse(installed.Version);
                SemanticVersion minimumVersion = SemanticVersion.Parse(dependency.MinimumVersion);
                SemanticVersion? maximumVersion = dependency.MaximumVersion is null
                    ? null
                    : SemanticVersion.Parse(dependency.MaximumVersion);

                if (installedVersion.CompareTo(minimumVersion) < 0
                    || maximumVersion is SemanticVersion maximum
                    && installedVersion.CompareTo(maximum) > 0)
                {
                    errors.Add(new AppCatalogError(
                        "catalog.dependency.incompatible",
                        manifest.Id,
                        $"Dependency '{dependency.AppId}' version '{installed.Version}' is outside the supported range."));
                }
            }
        }
    }

    /// <summary>Everything a catalog holds, swapped as one by <see cref="ReplaceContents"/>.</summary>
    private sealed record CatalogState(
        IReadOnlyDictionary<string, AppManifest> Manifests,
        IReadOnlyList<string> ActivationOrder,
        IReadOnlyList<string> DeactivationOrder,
        IReadOnlySet<string> SystemAppIds,
        IReadOnlyList<AppCatalogRejection> Rejected,
        IReadOnlyDictionary<string, string> CommandOwners,
        IReadOnlyDictionary<string, IReadOnlyList<AppComponentProvider>> ComponentProviders,
        IReadOnlyList<AppCatalogError> Warnings,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, AppManifestText>> Localized);

    private static IReadOnlyList<string>? CreateActivationOrder(
        IReadOnlyDictionary<string, AppManifest> manifests,
        List<AppCatalogError> errors)
    {
        Dictionary<string, int> dependencyCounts = manifests.Keys.ToDictionary(
            appId => appId,
            _ => 0,
            StringComparer.Ordinal);
        Dictionary<string, List<string>> dependents = manifests.Keys.ToDictionary(
            appId => appId,
            _ => new List<string>(),
            StringComparer.Ordinal);

        foreach (AppManifest manifest in manifests.Values)
        {
            foreach (AppDependencyManifest dependency in manifest.Dependencies)
            {
                if (!manifests.ContainsKey(dependency.AppId))
                {
                    continue;
                }

                dependencyCounts[manifest.Id]++;
                dependents[dependency.AppId].Add(manifest.Id);
            }
        }

        SortedSet<string> ready = new(
            dependencyCounts.Where(item => item.Value == 0).Select(item => item.Key),
            StringComparer.Ordinal);
        List<string> order = new(manifests.Count);

        while (ready.Count > 0)
        {
            string appId = ready.Min!;
            ready.Remove(appId);
            order.Add(appId);

            foreach (string dependent in dependents[appId].OrderBy(value => value, StringComparer.Ordinal))
            {
                dependencyCounts[dependent]--;
                if (dependencyCounts[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        if (order.Count == manifests.Count)
        {
            return order;
        }

        string[] cycleMembers = dependencyCounts
            .Where(item => item.Value > 0)
            .Select(item => item.Key)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        errors.Add(new AppCatalogError(
            "catalog.dependency.cycle",
            string.Join(',', cycleMembers),
            $"Dependency cycle detected among: {string.Join(", ", cycleMembers)}."));
        return null;
    }
}

/// <summary>
/// Describes a catalog construction failure.
/// </summary>
/// <param name="Code">Stable machine-readable error code.</param>
/// <param name="AppId">App associated with the error, when available.</param>
/// <param name="Message">Human-readable diagnostic.</param>
public sealed record AppCatalogError(string Code, string AppId, string Message);

/// <summary>One app's registration of an embeddable component (ADR 0063), as kept by <see cref="AppCatalog.ComponentProviders"/>.</summary>
/// <param name="AppId">The providing app.</param>
/// <param name="Component">The registration from its manifest.</param>
public sealed record AppComponentProvider(string AppId, AppComponentManifest Component);

/// <summary>
/// An app the host shipped but the catalog refuses to load (ADR 0098 §3); see <see cref="AppCatalog.Rejected"/>.
/// </summary>
/// <param name="Code">Stable machine-readable reason: <c>catalog.sdk.incompatible</c> or <c>catalog.dependency.unavailable</c>.</param>
/// <param name="Manifest">The refused app's manifest, for display (its name and requested SDK range).</param>
/// <param name="Message">Human-readable reason.</param>
public sealed record AppCatalogRejection(string Code, AppManifest Manifest, string Message)
{
    /// <summary>Gets the refused app's id.</summary>
    public string AppId => Manifest.Id;
}

/// <summary>
/// Contains either a complete immutable catalog or deterministic build errors.
/// </summary>
/// <param name="Catalog">Constructed catalog when successful.</param>
/// <param name="Errors">Catalog errors in deterministic order.</param>
public sealed record AppCatalogBuildResult(
    AppCatalog? Catalog,
    IReadOnlyList<AppCatalogError> Errors)
{
    /// <summary>Gets whether catalog construction succeeded.</summary>
    public bool IsSuccess => Catalog is not null && Errors.Count == 0;
}