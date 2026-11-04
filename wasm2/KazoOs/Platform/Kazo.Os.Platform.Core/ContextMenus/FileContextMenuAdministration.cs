using System.Globalization;
using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Platform.Core.ContextMenus;

/// <summary>One registered entry with the session user's choice, as Settings lists it (ADR 0159 §8).</summary>
/// <param name="AppId">The app that registered it.</param>
/// <param name="AppName">That app's display name.</param>
/// <param name="Entry">The registration.</param>
/// <param name="Title">The title in the requested language.</param>
/// <param name="IsHidden">Whether the session user hid it.</param>
public sealed record FileMenuEntryOverview(string AppId, string AppName, AppContextMenuEntryManifest Entry, string Title, bool IsHidden);

/// <summary>
/// The service behind <c>HackFileMenuEntries</c> (ADR 0159 §8): joins the entries of
/// <see cref="FileContextMenuRegistry"/> with the user's <see cref="FileContextMenuPreferences"/>.
/// </summary>
public sealed class FileContextMenuAdministration
{
    private readonly FileContextMenuRegistry _registry;
    private readonly FileContextMenuPreferences _preferences;
    private readonly AppCatalog _catalog;

    /// <summary>Creates the service.</summary>
    /// <param name="registry">The registered entries.</param>
    /// <param name="preferences">The session user's hidden entries.</param>
    /// <param name="catalog">The catalog, for the apps' names.</param>
    public FileContextMenuAdministration(FileContextMenuRegistry registry, FileContextMenuPreferences preferences, AppCatalog catalog)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>Lists every registered entry, in installation rank then declaration order.</summary>
    /// <param name="culture">The language of the titles.</param>
    /// <param name="cancellationToken">Cancels the read of the preferences.</param>
    public async ValueTask<IReadOnlyList<FileMenuEntryOverview>> GetOverviewAsync(CultureInfo culture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(culture);
        IReadOnlySet<string> hidden = await _preferences.GetHiddenAsync(cancellationToken).ConfigureAwait(false);
        return [.. _registry.Apps.SelectMany(app => app.Entries.Select(entry => new FileMenuEntryOverview(
            app.AppId,
            _catalog.Manifests.TryGetValue(app.AppId, out AppManifest? manifest) ? manifest.Name : app.AppId,
            entry,
            FileContextMenuMatcher.TitleOf(entry, culture),
            hidden.Contains(FileContextMenuPreferences.KeyOf(app.AppId, entry.Name)))))];
    }

    /// <summary>Hides or shows one registered entry for the session user.</summary>
    /// <param name="appId">The app.</param>
    /// <param name="name">The entry's name.</param>
    /// <param name="hidden">Whether to hide it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException">No registered app has this entry.</exception>
    public ValueTask SetHiddenAsync(string appId, string name, bool hidden, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        bool known = _registry.Apps.Any(app => app.AppId == appId && app.Entries.Any(entry => entry.Name == name));

        // Showing an entry that is gone stays possible, so a stale line can always be cleaned up.
        if (hidden && !known)
        {
            throw new ArgumentException($"No installed app registered the file menu entry '{appId}.{name}'.", nameof(name));
        }

        return _preferences.SetHiddenAsync(appId, name, hidden, cancellationToken);
    }
}
