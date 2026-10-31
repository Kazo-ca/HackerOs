using HackerOs.App.Abstractions;

namespace HackerOs.Platform.Core.Lifecycle;

/// <summary>Reports whether one app is currently allowed to launch or keep running.</summary>
public interface IAppEnablementRegistry
{
    /// <summary>Determines whether an app is currently enabled.</summary>
    /// <param name="appId">Exact app ID to check.</param>
    bool IsEnabled(string appId);

    /// <summary>Gets every currently disabled app ID.</summary>
    IReadOnlyCollection<string> DisabledAppIds { get; }

    /// <summary>
    /// Raised after apps were disabled or enabled again (an uninstall or reinstall, ADR 0048), so
    /// lists such as the launcher refresh without being reopened.
    /// </summary>
    event EventHandler? Changed;
}

/// <summary>Describes the outcome of a runtime disable request, per `P1-APP-011`.</summary>
/// <param name="Success">Whether the target app (and any cascaded dependents) is now disabled.</param>
/// <param name="DisabledAppIds">Every app ID newly disabled by this request, dependents first.</param>
/// <param name="Errors">Explanatory errors when <paramref name="Success"/> is <see langword="false"/>.</param>
/// <param name="StoppedProcesses">
/// The processes the request stopped (ADR 0097, section 5), each marked when it had to be forced
/// after its grace period; <see langword="null"/> is read as none.
/// </param>
public sealed record AppDisableResult(
    bool Success,
    IReadOnlyList<string> DisabledAppIds,
    IReadOnlyList<string> Errors,
    IReadOnlyList<AppProcessInfo>? StoppedProcesses = null);

/// <summary>One running process of an app, as an uninstall confirmation or report lists it (ADR 0097, section 5).</summary>
/// <param name="Pid">Process ID.</param>
/// <param name="AppId">App the process runs.</param>
/// <param name="Name">Name shown to the user: the app's name.</param>
/// <param name="UserName">Login name of the user it runs as.</param>
/// <param name="Forced">
/// In a report: whether the process did not finish its stop work within its grace period and was
/// cut off. Always <see langword="false"/> in a plan.
/// </param>
public sealed record AppProcessInfo(long Pid, string AppId, string Name, string UserName, bool Forced = false);

/// <summary>Describes the outcome of a runtime enable request, per `P1-APP-011`.</summary>
/// <param name="Success">Whether the app is now enabled.</param>
/// <param name="Errors">Explanatory errors when <paramref name="Success"/> is <see langword="false"/>.</param>
public sealed record AppEnableResult(bool Success, IReadOnlyList<string> Errors);

/// <summary>
/// Tracks which catalog apps are currently enabled and computes dependency-aware cascades for
/// runtime enable/disable requests. Never mutates the underlying <see cref="AppCatalog"/>.
/// </summary>
/// <remarks>
/// Since ADR 0048 this is no longer a durable state of its own: "disabled" means "uninstalled", and
/// <c>AppInstallationService</c> keeps this registry equal to the effective content of the two
/// <c>apps.config</c> files. It stays the single checkpoint every launch path consults.
/// </remarks>
public sealed class AppEnablementRegistry : IAppEnablementRegistry
{
    private readonly object _sync = new();
    private readonly AppCatalog _catalog;
    private readonly HashSet<string> _disabled = new(StringComparer.Ordinal);

    /// <summary>Initializes the registry with every catalog app enabled.</summary>
    /// <param name="catalog">
    /// Catalog whose apps this registry tracks. When apps join or leave it (ADR 0214 §1),
    /// <see cref="Changed"/> is raised too, so every app list listening here redraws.
    /// </param>
    public AppEnablementRegistry(AppCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _catalog.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public bool IsEnabled(string appId)
    {
        lock (_sync)
        {
            return _catalog.Manifests.ContainsKey(appId) && !_disabled.Contains(appId);
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> DisabledAppIds
    {
        get
        {
            lock (_sync)
            {
                return _disabled.ToArray();
            }
        }
    }

    /// <summary>
    /// Computes every currently enabled app that transitively depends (directly or through a
    /// non-optional chain) on <paramref name="appId"/>, ordered dependents-before-dependencies
    /// using the catalog's deactivation order, followed by <paramref name="appId"/> itself.
    /// </summary>
    /// <param name="appId">Root app whose dependents to compute.</param>
    internal IReadOnlyList<string> ComputeDisableClosure(string appId)
    {
        HashSet<string> closure = new(StringComparer.Ordinal) { appId };
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (AppManifest manifest in _catalog.Manifests.Values)
            {
                if (closure.Contains(manifest.Id))
                {
                    continue;
                }

                bool dependsOnClosure = manifest.Dependencies.Any(
                    dependency => !dependency.Optional && closure.Contains(dependency.AppId));
                if (dependsOnClosure)
                {
                    closure.Add(manifest.Id);
                    changed = true;
                }
            }
        }

        lock (_sync)
        {
            return _catalog.DeactivationOrder
                .Where(id => closure.Contains(id) && !_disabled.Contains(id))
                .ToArray();
        }
    }

    /// <summary>
    /// Determines every non-optional dependency of <paramref name="appId"/> that is missing or
    /// currently disabled, blocking a re-enable request.
    /// </summary>
    /// <param name="appId">App being re-enabled.</param>
    internal IReadOnlyList<string> ComputeBlockingDependencies(string appId)
    {
        if (!_catalog.Manifests.TryGetValue(appId, out AppManifest? manifest))
        {
            return [];
        }

        lock (_sync)
        {
            return manifest.Dependencies
                .Where(dependency => !dependency.Optional)
                .Where(dependency => !_catalog.Manifests.ContainsKey(dependency.AppId) || _disabled.Contains(dependency.AppId))
                .Select(dependency => dependency.AppId)
                .ToArray();
        }
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>Marks every app ID disabled. Used only after the caller has stopped their processes.</summary>
    public void MarkDisabled(IEnumerable<string> appIds)
    {
        bool changed = false;
        lock (_sync)
        {
            foreach (string appId in appIds)
            {
                changed |= _disabled.Add(appId);
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Marks one app ID enabled.</summary>
    public void MarkEnabled(string appId)
    {
        bool changed;
        lock (_sync)
        {
            changed = _disabled.Remove(appId);
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
