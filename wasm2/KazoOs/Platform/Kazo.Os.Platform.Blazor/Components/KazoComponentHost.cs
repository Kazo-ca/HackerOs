using System.Diagnostics.CodeAnalysis;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Blazor.Components;
using Kazo.Os.Platform.Core;
using Kazo.Os.Platform.Core.ComponentProviders;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Platform.Core.Lifecycle;

namespace Kazo.Os.Platform.Blazor.Components;

/// <summary>
/// A component the platform provides itself for a contract, without a manifest (ADR 0148 §3.2),
/// such as the OS context menu.
/// </summary>
public sealed class KazoPlatformComponent
{
    /// <summary>Initializes a platform registration.</summary>
    /// <param name="contract">The contract name.</param>
    /// <param name="componentType">The component, deriving from <see cref="KazoEmbeddableComponent"/>.</param>
    public KazoPlatformComponent(
        string contract,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
        Type componentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contract);
        ArgumentNullException.ThrowIfNull(componentType);
        if (componentType.IsAbstract || !typeof(KazoEmbeddableComponent).IsAssignableFrom(componentType))
        {
            throw new ArgumentException($"'{componentType.FullName}' is not an embeddable component.", nameof(componentType));
        }

        Contract = contract;
        ComponentType = componentType;
    }

    /// <summary>Gets the contract name.</summary>
    public string Contract { get; }

    /// <summary>Gets the component the platform renders for the contract.</summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
    public Type ComponentType { get; }
}

/// <summary>
/// The OS registry of embeddable components (ADR 0063, ADR 0148 §3): the providers the catalog read
/// from the installed apps' manifests at boot (<see cref="AppCatalog.ComponentProviders"/>), plus the
/// platform's own. It loads an app provider's assembly the way a launch does and checks the type
/// before a window renders it. Like KDE's KParts trader, the host asks for a contract, never for an app.
/// </summary>
public sealed class KazoComponentHost : IHackComponentHost
{
    private readonly AppCatalog _catalog;
    private readonly IAppEnablementRegistry _enablement;
    private readonly Func<string, CancellationToken, Task<(AppDescriptor? Descriptor, string? Error)>> _descriptors;
    private readonly Dictionary<string, KazoPlatformComponent> _platform = new(StringComparer.Ordinal);
    private readonly Action<string>? _reportError;
    private readonly HashSet<string> _reportedFallbacks = new(StringComparer.Ordinal);

    /// <summary>Initializes the registry over the host catalog, with no platform component and no system log.</summary>
    /// <param name="catalog">Build-known apps and their manifests.</param>
    /// <param name="enablement">Installed state: an uninstalled app provides nothing.</param>
    /// <param name="descriptors">Gets an app's descriptor, lazy-loading its assembly (<c>AppLifecycleOrchestrator.EnsureDescriptorAsync</c>).</param>
    public KazoComponentHost(
        AppCatalog catalog,
        IAppEnablementRegistry enablement,
        Func<string, CancellationToken, Task<(AppDescriptor? Descriptor, string? Error)>> descriptors)
        : this(catalog, enablement, descriptors, platformComponents: null, reportError: null)
    {
    }

    /// <summary>Initializes the registry over the host catalog.</summary>
    /// <param name="catalog">Build-known apps and their manifests.</param>
    /// <param name="enablement">Installed state: an uninstalled app provides nothing.</param>
    /// <param name="descriptors">Gets an app's descriptor, lazy-loading its assembly (<c>AppLifecycleOrchestrator.EnsureDescriptorAsync</c>).</param>
    /// <param name="platformComponents">The components the platform provides itself (ADR 0148 §3.2); tried before any app.</param>
    /// <param name="reportError">
    /// Writes a line to the system log (<c>/var/log/syslog</c>): an OS contract with no
    /// working provider is an error of the OS, never a normal state (ADR 0148 §5).
    /// </param>
    public KazoComponentHost(
        AppCatalog catalog,
        IAppEnablementRegistry enablement,
        Func<string, CancellationToken, Task<(AppDescriptor? Descriptor, string? Error)>> descriptors,
        IEnumerable<KazoPlatformComponent>? platformComponents,
        Action<string>? reportError)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _enablement = enablement ?? throw new ArgumentNullException(nameof(enablement));
        _descriptors = descriptors ?? throw new ArgumentNullException(nameof(descriptors));
        _reportError = reportError;
        foreach (KazoPlatformComponent component in platformComponents ?? [])
        {
            if (!_platform.TryAdd(component.Contract, component))
            {
                throw new ArgumentException($"The platform registers '{component.Contract}' twice.", nameof(platformComponents));
            }
        }
    }

    /// <summary>
    /// Gets the administrator's choice of provider (<c>/etc/hackeros/components.config</c>,
    /// ADR 0156 §2), tried first for the contracts an administrator chooses; <see langword="null"/>
    /// keeps the catalog order (the File Explorer first).
    /// </summary>
    public IComponentProviderSelection? Selection { get; init; }

    /// <summary>
    /// Gets the writer of warnings to the system log (<c>/var/log/syslog</c>, source
    /// <c>components</c>): <c>component.fallback</c> when the chosen provider does not load and
    /// another one takes over (ADR 0156 §4). <see langword="null"/> writes nothing.
    /// </summary>
    public Action<string>? ReportWarning { get; init; }

    /// <summary>
    /// Lists the installed apps registering <paramref name="contract"/>, in catalog order: system
    /// apps first (the OS's own provider), then by app id. The administrator's choice, which
    /// <see cref="ResolveAsync"/> tries first, is not applied here; platform components are not apps
    /// and are not listed.
    /// </summary>
    /// <param name="contract">Contract name.</param>
    public IReadOnlyList<(string AppId, AppComponentManifest Component)> GetProviders(string contract) =>
        AppComponentContracts.GetProviderScope(contract) == AppComponentProviderScope.Platform
            ? []
            : [.. _catalog.GetComponentProviders(contract)
                .Where(provider => _enablement.IsEnabled(provider.AppId))
                .Select(provider => (provider.AppId, provider.Component))];

    /// <inheritdoc />
    public async Task<KazoComponentResolution> ResolveAsync(string contract, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contract);
        if (_platform.TryGetValue(contract, out KazoPlatformComponent? platform))
        {
            return KazoComponentResolution.Resolved(contract, platform.ComponentType, KazoComponentResolution.PlatformProviderId);
        }

        List<(string AppId, AppComponentManifest Component)> providers = [.. GetProviders(contract)];
        string? selected = await GetSelectedProviderAsync(contract, cancellationToken).ConfigureAwait(false);
        string? selectedFailure = null;
        if (selected is not null)
        {
            // ADR 0156 §2: the administrator's choice is tried first, the others stay fallbacks.
            int index = providers.FindIndex(provider => provider.AppId == selected);
            if (index > 0)
            {
                (string AppId, AppComponentManifest Component) chosen = providers[index];
                providers.RemoveAt(index);
                providers.Insert(0, chosen);
            }
            else if (index < 0)
            {
                selectedFailure = _catalog.GetComponentProviders(contract).Any(provider => provider.AppId == selected)
                    ? $"'{selected}' is disabled or uninstalled for this user"
                    : $"'{selected}' is not an installed provider of '{contract}'";
            }
        }

        if (providers.Count == 0)
        {
            return Report(KazoComponentResolution.Unavailable(contract, $"No installed app provides '{contract}'."));
        }

        // The first provider that loads wins; the reason of the first failure is kept for the user.
        KazoComponentResolution? firstFailure = null;
        foreach ((string appId, AppComponentManifest component) in providers)
        {
            KazoComponentResolution resolution = await ResolveProviderAsync(contract, appId, component, cancellationToken)
                .ConfigureAwait(false);
            if (resolution.IsResolved)
            {
                if (selected is not null && appId != selected)
                {
                    ReportFallback(contract, selected, selectedFailure ?? firstFailure?.Error, appId);
                }

                return resolution;
            }

            firstFailure ??= resolution;
        }

        return Report(firstFailure!);
    }

    /// <summary>Reads the administrator's choice; an unreadable file is the default, never a failure.</summary>
    private async Task<string?> GetSelectedProviderAsync(string contract, CancellationToken cancellationToken)
    {
        if (Selection is null
            || AppComponentContracts.GetProviderScope(contract) != AppComponentProviderScope.AdministratorSelected)
        {
            return null;
        }

        try
        {
            return await Selection.GetSelectedProviderAsync(contract, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportWarning?.Invoke($"component.selection: '{contract}': the choice of provider could not be read ({exception.Message}); using the default");
            return null;
        }
    }

    /// <summary>
    /// Writes <c>component.fallback</c> (ADR 0156 §4) once per contract, chosen app and provider used,
    /// for the life of this registry: every control shown would otherwise repeat the same line. The
    /// line stays in the file, so the chosen app is used again as soon as it loads.
    /// </summary>
    private void ReportFallback(string contract, string selected, string? reason, string used)
    {
        if (ReportWarning is not null && _reportedFallbacks.Add($"{contract}|{selected}|{used}"))
        {
            ReportWarning($"component.fallback: '{contract}': the chosen provider '{selected}' did not load ({reason ?? "unknown reason"}); using '{used}'");
        }
    }

    /// <summary>
    /// Writes an unavailable OS contract to the system log (ADR 0148 §5, ADR 0156 §4): the file
    /// view, the file dialogs and the context menu are the OS's own controls, so no working
    /// provider at all is a fault to investigate. An open contract without a
    /// provider (no terminal installed, say) is a normal state and is only shown to the user.
    /// </summary>
    private KazoComponentResolution Report(KazoComponentResolution resolution)
    {
        if (_reportError is not null
            && AppComponentContracts.GetProviderScope(resolution.Contract) != AppComponentProviderScope.AnyApp)
        {
            _reportError($"component.unavailable: '{resolution.Contract}' has no working provider: {resolution.Error}");
        }

        return resolution;
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Component types come from build-known manifests whose assemblies a trimmed host keeps whole (ADR 0113: generated root descriptor).")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "Component types come from build-known manifests whose assemblies a trimmed host keeps whole (ADR 0113: generated root descriptor).")]
    private async Task<KazoComponentResolution> ResolveProviderAsync(
        string contract, string appId, AppComponentManifest component, CancellationToken cancellationToken)
    {
        (AppDescriptor? descriptor, string? error) = await _descriptors(appId, cancellationToken).ConfigureAwait(false);
        if (descriptor is null)
        {
            return KazoComponentResolution.Unavailable(contract, error ?? $"The app '{appId}' could not be loaded.", appId);
        }

        // The manifest validator keeps the component in the entry-point assembly, the one lazy
        // loading has just fetched.
        Type? type = descriptor.Assembly.GetType(component.Type, throwOnError: false);
        if (type is null || type.IsAbstract || !typeof(KazoEmbeddableComponent).IsAssignableFrom(type))
        {
            return KazoComponentResolution.Unavailable(
                contract,
                $"The app '{appId}' registers '{component.Type}', which is not an embeddable component.",
                appId);
        }

        return KazoComponentResolution.Resolved(contract, type, appId);
    }
}
