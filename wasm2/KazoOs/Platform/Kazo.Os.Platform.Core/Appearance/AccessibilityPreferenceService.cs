using Kazo.Os.AppSdk.Accessibility;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Appearance;

/// <summary>
/// The shell's live view of the accessibility preferences (ADR 0105): the layered
/// <c>accessibility.config</c> (the session user's <c>~/.config/hackeros/</c> over the machine's
/// <c>/etc/hackeros/</c>), resolved against the host's own settings and against
/// <c>animationsEnabled</c> of <c>appearance.config</c>. The theme reads <see cref="Effective"/>,
/// apps read it through <see cref="HackAccessibility"/>.
/// </summary>
/// <remarks>
/// Before a sign-in no user layer exists, so the sign-in screens follow <c>/etc</c> alone, as the
/// ADR asks; the settings service switches layers when the session opens or closes and raises a
/// change, which this service follows like <see cref="ThemePreferenceService"/> does.
/// </remarks>
public sealed class AccessibilityPreferenceService : IDisposable
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly IOsSettingsService _settings;
    private readonly ThemePreferenceService _appearance;
    private readonly IHostAccessibilityProbe _host;

    /// <summary>Creates the service.</summary>
    /// <param name="settings">OS settings service.</param>
    /// <param name="appearance">The appearance preferences, for <c>animationsEnabled</c>.</param>
    /// <param name="host">The host's own accessibility settings.</param>
    public AccessibilityPreferenceService(IOsSettingsService settings, ThemePreferenceService appearance, IHostAccessibilityProbe host)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _appearance = appearance ?? throw new ArgumentNullException(nameof(appearance));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings.DocumentChanged += OnDocumentChanged;
        _appearance.Changed += OnInputsChanged;
        _host.Changed += OnInputsChanged;
    }

    /// <summary>Gets what the files ask for, defaulting safely until initialization completes.</summary>
    public AccessibilitySettings Settings { get; private set; } = AccessibilitySettings.Default;

    /// <summary>Gets the last merged snapshot, exposing each key's source layer and lock state.</summary>
    public LayeredConfigSnapshot? Snapshot { get; private set; }

    /// <summary>Gets the effective preferences: <see cref="Settings"/> with every <c>auto</c> resolved.</summary>
    public HackAccessibilityPreferences Effective { get; private set; } = HackAccessibilityPreferences.Default;

    /// <summary>Raised after <see cref="Effective"/> changed.</summary>
    public event Action? Changed;

    /// <summary>Reads the host, loads the files and starts watching them, without raising <see cref="Changed"/>.</summary>
    /// <param name="cancellationToken">Signals cancellation of the reads.</param>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _host.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await RefreshCoreAsync(raiseEvent: false, cancellationToken).ConfigureAwait(false);
        await _settings.WatchAsync(AccessibilitySettingsDocuments.Document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reloads the file and notifies subscribers when the effective preferences changed.</summary>
    /// <param name="cancellationToken">Signals cancellation of the read.</param>
    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(raiseEvent: true, cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        _settings.DocumentChanged -= OnDocumentChanged;
        _appearance.Changed -= OnInputsChanged;
        _host.Changed -= OnInputsChanged;
        _refreshGate.Dispose();
    }

    private void OnDocumentChanged(object? sender, OsSettingsChangedEventArgs args)
    {
        if (args.Concerns(AccessibilitySettingsDocuments.Document.DocumentId))
        {
            _ = RefreshInBackgroundAsync();
        }
    }

    // The appearance file and the host change nothing on disk here: only the resolution moves.
    private void OnInputsChanged() => Apply(Settings, Snapshot, raiseEvent: true);

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
        // Serialized for the same reason as ThemePreferenceService: a stale read finishing last
        // would overwrite a fresh one.
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LayeredConfigSnapshot snapshot;
        try
        {
            snapshot = await _settings
                .ReadAsync(AccessibilitySettingsDocuments.Document, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }

        Apply(AccessibilitySettingsDocuments.ToSettings(snapshot), snapshot, raiseEvent);
    }

    private void Apply(AccessibilitySettings settings, LayeredConfigSnapshot? snapshot, bool raiseEvent)
    {
        HackAccessibilityPreferences effective = AccessibilitySettingsDocuments.Resolve(
            settings, _host.Current, _appearance.Current.AnimationsEnabled);
        bool changed = effective != Effective;
        Settings = settings;
        Snapshot = snapshot;
        Effective = effective;
        if (raiseEvent && changed)
        {
            Changed?.Invoke();
        }
    }
}
