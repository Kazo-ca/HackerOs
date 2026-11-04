using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.Platform.Core.Apps;
using Kazo.Os.Platform.Core.Intents;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Execution;

/// <summary>
/// Trusted <see cref="IAppCatalogGateway"/> (ADR 0056, section 2): reads <see cref="AppCatalog"/>
/// through the launch checkpoint <see cref="IAppEnablementRegistry"/>, so an app uninstalled by
/// <c>apps.config</c> (ADR 0048) is hidden here as everywhere else. No capability is checked for
/// the catalog: the list of installed apps is public. The default handlers (ADR 0121) are read and
/// written for the app's user, behind <c>file-associations.read</c> (and
/// <c>filesystem.user-home.write</c> for a change).
/// </summary>
/// <remarks>
/// <see cref="Changed"/> forwards <see cref="IAppInstallationService.Changed"/>. The installation
/// service depends (through the lifecycle orchestrator) on the context factory that builds this
/// gateway, so it is resolved lazily, on the first subscription.
/// </remarks>
internal sealed class AppCatalogGateway : IAppCatalogGateway
{
    private readonly AppCatalog _catalog;
    private readonly IAppEnablementRegistry _enablement;
    private readonly Func<FileAssociationResolver>? _resolver;
    private readonly Func<IAppInstallationService>? _installation;
    private readonly AuthenticatedPrincipal? _user;
    private readonly ICapabilityChecker? _capabilities;
    private readonly AppAmbientContextBinding _ambient = new();
    private readonly Lock _gate = new();
    private EventHandler? _changed;
    private IAppInstallationService? _subscribedTo;

    /// <summary>Creates the gateway for one app instance.</summary>
    /// <param name="catalog">Installed apps.</param>
    /// <param name="enablement">Launch checkpoint.</param>
    /// <param name="resolver">File association resolver, when the host has one.</param>
    /// <param name="installation">Installation service, resolved on the first <see cref="Changed"/> subscription.</param>
    /// <param name="user">User the app runs as: whose own default handlers apply (ADR 0121).</param>
    /// <param name="capabilities">The app's capability checker, for the default handlers.</param>
    public AppCatalogGateway(
        AppCatalog catalog,
        IAppEnablementRegistry enablement,
        Func<FileAssociationResolver>? resolver,
        Func<IAppInstallationService>? installation,
        AuthenticatedPrincipal? user = null,
        ICapabilityChecker? capabilities = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _enablement = enablement ?? throw new ArgumentNullException(nameof(enablement));
        _resolver = resolver;
        _installation = installation;
        _user = user;
        _capabilities = capabilities;
    }

    /// <inheritdoc />
    public event EventHandler? Changed
    {
        add
        {
            lock (_gate)
            {
                _changed += value;
                if (_subscribedTo is null && _installation is not null)
                {
                    _subscribedTo = _installation();
                    _subscribedTo.Changed += OnInstallationChanged;
                }
            }
        }
        remove
        {
            lock (_gate)
            {
                _changed -= value;
                if (_changed is null && _subscribedTo is not null)
                {
                    _subscribedTo.Changed -= OnInstallationChanged;
                    _subscribedTo = null;
                }
            }
        }
    }

    /// <summary>Binds the app context <see cref="Changed"/> handlers run under; called once by the factory.</summary>
    internal void BindAmbientContext(Kazo.Os.AppSdk.IAppExecutionContext context) => _ambient.Bind(context);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<HackAppInfo>> GetInstalledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<HackAppInfo> installed = [.. _catalog.Manifests.Values
            .Where(manifest => _enablement.IsEnabled(manifest.Id))
            .Select(Describe)
            .OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(info => info.Id, StringComparer.Ordinal)];
        return ValueTask.FromResult(installed);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HackAppInfo>> GetHandlersAsync(
        VirtualPath path, string? mediaType = null, CancellationToken cancellationToken = default)
    {
        if (_resolver is null)
        {
            throw new NotSupportedException("This execution context factory was not configured with a file association resolver.");
        }

        FileHandlerCandidates candidates = await _resolver().GetHandlerAppIdsAsync(path, _user, mediaType, cancellationToken).ConfigureAwait(false);

        // The default (if any) stays first; the others are sorted by name, as a user reads them.
        IEnumerable<HackAppInfo> others = candidates.AppIds
            .Where(id => !string.Equals(id, candidates.DefaultAppId, StringComparison.Ordinal))
            .Select(id => Describe(_catalog.Manifests[id]))
            .OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase);
        return candidates.DefaultAppId is { } defaultAppId
            ? [Describe(_catalog.Manifests[defaultAppId]), .. others]
            : [.. others];
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HackFileAssociation>> GetDefaultHandlersAsync(CancellationToken cancellationToken = default)
    {
        FileAssociationResolver resolver = RequireAssociations(AppCapabilities.FileAssociationsRead);
        IReadOnlyList<ConfiguredFileAssociation> rows = await resolver.GetConfiguredDefaultsAsync(_user, cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(static row => new HackFileAssociation(
            row.Target,
            row.Action,
            row.AppId,
            row.Layer == FileAssociationLayer.User ? HackFileAssociationLayer.User : HackFileAssociationLayer.System))];
    }

    /// <inheritdoc />
    public async ValueTask<HackDefaultHandlerStatus> SetUserDefaultHandlerAsync(
        string target, string action, string? appId, CancellationToken cancellationToken = default)
    {
        FileAssociationResolver resolver = RequireAssociations(AppCapabilities.FileAssociationsRead);
        _capabilities!.Require(AppCapabilities.FileSystemUserHomeWrite);
        if (_user is null)
        {
            return HackDefaultHandlerStatus.WriteFailed;
        }

        UserDefaultHandlerChangeStatus status = await resolver
            .SetUserDefaultAsync(_user, target ?? string.Empty, action ?? string.Empty, appId, cancellationToken)
            .ConfigureAwait(false);
        return status switch
        {
            UserDefaultHandlerChangeStatus.Changed => HackDefaultHandlerStatus.Changed,
            UserDefaultHandlerChangeStatus.InvalidTarget => HackDefaultHandlerStatus.InvalidTarget,
            UserDefaultHandlerChangeStatus.InvalidAction => HackDefaultHandlerStatus.InvalidAction,
            UserDefaultHandlerChangeStatus.AppNotFound => HackDefaultHandlerStatus.AppNotFound,
            UserDefaultHandlerChangeStatus.AppCannotHandle => HackDefaultHandlerStatus.AppCannotHandle,
            UserDefaultHandlerChangeStatus.FileInvalid => HackDefaultHandlerStatus.FileInvalid,
            _ => HackDefaultHandlerStatus.WriteFailed
        };
    }

    /// <summary>Checks <paramref name="capability"/> and returns the resolver, or explains why the host cannot answer.</summary>
    private FileAssociationResolver RequireAssociations(string capability)
    {
        if (_resolver is null || _capabilities is null)
        {
            throw new NotSupportedException("This execution context factory was not configured with a file association resolver.");
        }

        _capabilities.Require(capability);
        return _resolver();
    }

    /// <summary>
    /// Describes an app in the calling process's language (ADR 0104 section 4): <c>LANG=fr_CA.UTF-8</c>
    /// lists "Calculatrice", <c>LANG=en_US.UTF-8</c> "Calculator".
    /// </summary>
    private HackAppInfo Describe(AppManifest manifest) => HackAppInfo.FromManifest(
        _catalog.Localize(manifest, _ambient.Context?.Culture ?? Kazo.Os.AppSdk.Globalization.HackLocale.DefaultLanguage),
        _catalog.IsSystemApp(manifest.Id));

    private void OnInstallationChanged(object? sender, EventArgs e)
    {
        EventHandler? handlers;
        lock (_gate)
        {
            handlers = _changed;
        }

        if (handlers is not null)
        {
            _ambient.Wrap(() => handlers(this, EventArgs.Empty))();
        }
    }
}
