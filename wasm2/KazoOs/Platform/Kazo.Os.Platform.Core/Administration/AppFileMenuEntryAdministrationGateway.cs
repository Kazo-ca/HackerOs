using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.Platform.Core.ContextMenus;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Platform.Core.Administration;

/// <summary>
/// Trusted <see cref="IAppFileMenuEntryAdministrationGateway"/> (ADR 0159 §8) over
/// <see cref="FileContextMenuAdministration"/>.
/// </summary>
/// <remarks>
/// Every call requires <c>apps.manage</c>. The choice is the session user's own preference
/// (<c>~/.config/hackeros/context-menus.config</c>), so no administrator is asked.
/// </remarks>
internal sealed class AppFileMenuEntryAdministrationGateway : IAppFileMenuEntryAdministrationGateway
{
    private readonly Func<FileContextMenuAdministration>? _service;
    private readonly ICapabilityChecker _capabilities;
    private readonly Func<CultureInfo> _language;

    /// <summary>Creates the gateway for one app instance.</summary>
    /// <param name="service">Lazily resolved service; <see langword="null"/> makes every call unsupported.</param>
    /// <param name="capabilities">The calling instance's capabilities.</param>
    /// <param name="language">The calling instance's language, for the titles.</param>
    public AppFileMenuEntryAdministrationGateway(
        Func<FileContextMenuAdministration>? service,
        ICapabilityChecker capabilities,
        Func<CultureInfo> language)
    {
        _service = service;
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _language = language ?? throw new ArgumentNullException(nameof(language));
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HackFileMenuEntryInfo>> GetEntriesAsync(CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.AppsManage);
        IReadOnlyList<FileMenuEntryOverview> overview = await Service().GetOverviewAsync(_language(), cancellationToken).ConfigureAwait(false);
        return [.. overview.Select(item => new HackFileMenuEntryInfo(
            item.AppId, item.AppName, item.Entry.Name, item.Title, item.Entry.Parent, item.Entry.Scopes, item.IsHidden))];
    }

    /// <inheritdoc />
    public ValueTask SetHiddenAsync(string appId, string name, bool hidden, CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.AppsManage);
        return Service().SetHiddenAsync(appId, name, hidden, cancellationToken);
    }

    private FileContextMenuAdministration Service() =>
        _service?.Invoke() ?? throw new NotSupportedException("This host does not list the file menu entries.");
}
