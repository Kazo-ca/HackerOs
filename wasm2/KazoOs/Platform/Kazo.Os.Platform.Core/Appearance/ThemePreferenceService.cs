using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;
using Kazo.Os.Theming.Abstractions;

namespace Kazo.Os.Platform.Core.Appearance;

/// <summary>
/// Maintains the shell's live view of the layered appearance settings
/// (<see cref="AppearanceSettingsDocuments.Document"/>): the session user's
/// <c>~/.config/kazoos/appearance.config</c> over the machine's <c>/etc/kazoos/appearance.config</c>.
/// Writes go through <see cref="SetAsync"/>, so the file's permissions decide who may change what.
/// </summary>
public sealed class ThemePreferenceService : IDisposable
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly IOsSettingsService _settings;

    /// <summary>Creates the service over the OS settings files.</summary>
    /// <param name="settings">OS settings service.</param>
    public ThemePreferenceService(IOsSettingsService settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settings.DocumentChanged += OnDocumentChanged;
    }

    /// <summary>Gets the current preferences, defaulting safely until initialization completes.</summary>
    public ThemePreferences Current { get; private set; } = ThemePreferences.Default;

    /// <summary>Gets the last merged snapshot, exposing each key's source layer and lock state.</summary>
    public LayeredConfigSnapshot? Snapshot { get; private set; }

    /// <summary>Raised after a refresh changes at least one effective preference.</summary>
    public event Action? Changed;

    /// <summary>Loads the initial appearance and starts watching its files, without emitting a change notification.</summary>
    /// <param name="cancellationToken">Signals cancellation of the settings read.</param>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await RefreshCoreAsync(raiseEvent: false, cancellationToken).ConfigureAwait(false);
        await _settings.WatchAsync(AppearanceSettingsDocuments.Document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reloads the appearance files and notifies subscribers when the effective value changed.</summary>
    /// <param name="cancellationToken">Signals cancellation of the settings read.</param>
    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(raiseEvent: true, cancellationToken);

    /// <summary>Writes every preference that differs from the current effective value to one layer.</summary>
    /// <param name="preferences">Desired preferences.</param>
    /// <param name="target">Layer to write; the user layer by default.</param>
    /// <param name="writer">Optional caller gateway whose rights decide the write (see <see cref="IOsSettingsService.SetAsync"/>).</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    public async Task SetAsync(
        ThemePreferences preferences,
        SettingLayer target = SettingLayer.User,
        Simulation.Abstractions.Gateways.IAppFileSystemGateway? writer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        OsSettingsDocument document = AppearanceSettingsDocuments.Document;
        (string Key, string Value)[] desired =
        [
            (AppearanceSettingsDocuments.DesktopThemeKey, preferences.DesktopThemeId),
            (AppearanceSettingsDocuments.MobileThemeKey, preferences.MobileThemeId),
            (AppearanceSettingsDocuments.AccentKey, preferences.AccentId),
            (AppearanceSettingsDocuments.AnimationsKey, preferences.AnimationsEnabled ? "true" : "false")
        ];

        // The user layer only records what differs from the effective value, so untouched keys keep
        // following the administrator's defaults. A system write records the whole set, in one edit,
        // because it defines the default every user starts from.
        LayeredConfigSnapshot current = Snapshot ?? await _settings.ReadAsync(document, cancellationToken).ConfigureAwait(false);
        KeyValuePair<string, string>[] changes =
        [
            .. desired
                .Where(entry => target == SettingLayer.System || current.Values[entry.Key] != entry.Value)
                .Select(entry => new KeyValuePair<string, string>(entry.Key, entry.Value))
        ];
        await _settings.SetManyAsync(document, changes, target, writer, cancellationToken).ConfigureAwait(false);

        await RefreshCoreAsync(raiseEvent: true, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _settings.DocumentChanged -= OnDocumentChanged;
        _refreshGate.Dispose();
    }

    private void OnDocumentChanged(object? sender, OsSettingsChangedEventArgs args)
    {
        if (args.Concerns(AppearanceSettingsDocuments.Document.DocumentId))
        {
            _ = RefreshInBackgroundAsync();
        }
    }

    private async Task RefreshInBackgroundAsync()
    {
        try
        {
            await RefreshCoreAsync(raiseEvent: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Keep the last good preferences; the read problem is already logged by the settings service.
        }
    }

    private async Task RefreshCoreAsync(bool raiseEvent, CancellationToken cancellationToken)
    {
        // Reloads are serialized: a change notification arriving mid-write would otherwise let a
        // stale read finish last and overwrite the fresh state.
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool changed;
        try
        {
            LayeredConfigSnapshot snapshot = await _settings
                .ReadAsync(AppearanceSettingsDocuments.Document, cancellationToken)
                .ConfigureAwait(false);
            ThemePreferences preferences = AppearanceSettingsDocuments.ToPreferences(snapshot);
            changed = preferences != Current;
            Snapshot = snapshot;
            Current = preferences;
        }
        finally
        {
            _refreshGate.Release();
        }

        if (raiseEvent && changed)
        {
            Changed?.Invoke();
        }
    }
}
