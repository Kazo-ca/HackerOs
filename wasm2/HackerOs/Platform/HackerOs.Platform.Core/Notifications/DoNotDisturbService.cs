using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.Settings;

namespace HackerOs.Platform.Core.Notifications;

/// <summary>
/// The "Do not disturb" mode of the session user (ADR 0216): while it is on, a new notification
/// shows no bubble and plays no sound, but still goes to the clock panel.
/// </summary>
/// <remarks>
/// The mode is the <c>doNotDisturb</c> key of the layered <c>notifications.config</c> document. The
/// document is watched, and the settings service raises a change when another user signs in, so
/// <see cref="IsEnabled"/> always follows the session user and an edit made with <c>nano</c>.
/// </remarks>
public sealed class DoNotDisturbService : IDisposable
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly IOsSettingsService _settings;

    /// <summary>Creates the service.</summary>
    /// <param name="settings">OS settings service (<c>notifications.config</c>).</param>
    public DoNotDisturbService(IOsSettingsService settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settings.DocumentChanged += OnDocumentChanged;
    }

    /// <summary>Gets whether "Do not disturb" is on for the session user.</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>Raised after <see cref="IsEnabled"/> changed; may be raised off the renderer's context.</summary>
    public event Action? Changed;

    /// <summary>Loads <c>notifications.config</c> and starts watching it.</summary>
    /// <param name="cancellationToken">Signals cancellation of the reads.</param>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        await _settings.WatchAsync(NotificationSettingsDocuments.Document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the document again and raises <see cref="Changed"/> when the mode changed.</summary>
    /// <param name="cancellationToken">Signals cancellation of the read.</param>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        // Serialized like OsSoundService: a stale read finishing last would win.
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool changed;
        try
        {
            LayeredConfigSnapshot snapshot = await _settings
                .ReadAsync(NotificationSettingsDocuments.Document, cancellationToken)
                .ConfigureAwait(false);
            bool enabled = NotificationSettingsDocuments.IsDoNotDisturb(snapshot);
            changed = enabled != IsEnabled;
            IsEnabled = enabled;
        }
        finally
        {
            _refreshGate.Release();
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Turns the mode on or off for the session user, in <c>~/.config/hackeros/notifications.config</c>.</summary>
    /// <param name="enabled">The new state.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="InvalidOperationException">No user is signed in, or an administrator locked the key.</exception>
    /// <exception cref="UnauthorizedAccessException">The file's permissions deny the write.</exception>
    public async Task SetAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _settings.SetAsync(
            NotificationSettingsDocuments.Document,
            NotificationSettingsDocuments.DoNotDisturbKey,
            enabled ? "true" : "false",
            SettingLayer.User,
            writer: null,
            cancellationToken).ConfigureAwait(false);
        // The watcher would tell too, but the switch must not wait for it.
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _settings.DocumentChanged -= OnDocumentChanged;
        _refreshGate.Dispose();
    }

    private void OnDocumentChanged(object? sender, OsSettingsChangedEventArgs args)
    {
        if (args.Concerns(NotificationSettingsDocuments.DocumentId))
        {
            _ = RefreshInBackgroundAsync();
        }
    }

    private async Task RefreshInBackgroundAsync()
    {
        try
        {
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            // Keep the last good state; the read problem is already logged by the settings service.
        }
    }
}
