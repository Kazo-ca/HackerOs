using HackerOs.AppSdk.Globalization;
using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.Settings;

namespace HackerOs.Platform.Core.Globalization;

/// <summary>
/// The session's time zone (ADR 0193 section 3): the layered <c>datetime.config</c> (the session
/// user's <c>~/.config/hackeros/</c> over the machine's <c>/etc/hackeros/</c>), resolved to a zone.
/// The taskbar clock, the clock panel and every app process without its own <c>TZ</c> read it.
/// </summary>
/// <remarks>
/// A change applies at once, as on GNOME: the settings service raises a change when the file is
/// written (or when the session opens or closes and the user layer comes or goes), and this service
/// re-reads it and raises <see cref="Changed"/>. <c>auto</c>, a missing file and an unknown name all
/// mean the device's zone, as the browser reports it.
/// </remarks>
public sealed class SessionTimeZoneService : IDisposable
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly IOsSettingsService _settings;
    private readonly TimeZoneInfo _device;

    /// <summary>Creates the service.</summary>
    /// <param name="settings">OS settings service.</param>
    public SessionTimeZoneService(IOsSettingsService settings)
        : this(settings, HackTimeZone.Device)
    {
    }

    /// <summary>Creates the service with an explicit device zone (tests).</summary>
    /// <param name="settings">OS settings service.</param>
    /// <param name="device">The zone <c>auto</c> stands for.</param>
    public SessionTimeZoneService(IOsSettingsService settings, TimeZoneInfo device)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        TimeZone = _device;
        _settings.DocumentChanged += OnDocumentChanged;
    }

    /// <summary>Gets the <c>timeZone</c> value of the files: <c>auto</c> or an IANA identifier.</summary>
    public string Setting { get; private set; } = HackTimeZone.Automatic;

    /// <summary>Gets the effective zone: the one <see cref="Setting"/> names, or the device's zone.</summary>
    public TimeZoneInfo TimeZone { get; private set; }

    /// <summary>Raised after <see cref="TimeZone"/> changed.</summary>
    public event Action? Changed;

    /// <summary>Converts <paramref name="time"/> to <see cref="TimeZone"/>.</summary>
    /// <param name="time">Any instant, usually a UTC timestamp of the OS.</param>
    public DateTimeOffset ToLocal(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, TimeZone);

    /// <summary>Loads the files and starts watching them, without raising <see cref="Changed"/>.</summary>
    /// <param name="cancellationToken">Signals cancellation of the reads.</param>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await RefreshCoreAsync(raiseEvent: false, cancellationToken).ConfigureAwait(false);
        await _settings.WatchAsync(TimeZoneSettingsDocuments.Document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reloads the files and notifies subscribers when the effective zone changed.</summary>
    /// <param name="cancellationToken">Signals cancellation of the read.</param>
    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(raiseEvent: true, cancellationToken);

    /// <summary>Sets the zone from a <c>timeZone</c> value directly (tests, and the refresh above).</summary>
    /// <param name="setting"><c>auto</c> or an IANA identifier.</param>
    public void Apply(string? setting) => Apply(setting, raiseEvent: true);

    /// <inheritdoc />
    public void Dispose()
    {
        _settings.DocumentChanged -= OnDocumentChanged;
        _refreshGate.Dispose();
    }

    private void OnDocumentChanged(object? sender, OsSettingsChangedEventArgs args)
    {
        if (args.Concerns(TimeZoneSettingsDocuments.Document.DocumentId))
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            // Keep the last good zone; the read problem is already logged by the settings service.
        }
    }

    private async Task RefreshCoreAsync(bool raiseEvent, CancellationToken cancellationToken)
    {
        // Serialized like AccessibilityPreferenceService: a stale read finishing last would win.
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LayeredConfigSnapshot snapshot;
        try
        {
            snapshot = await _settings.ReadAsync(TimeZoneSettingsDocuments.Document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }

        Apply(TimeZoneSettingsDocuments.ToSetting(snapshot), raiseEvent);
    }

    private void Apply(string? setting, bool raiseEvent)
    {
        string value = string.IsNullOrWhiteSpace(setting) ? HackTimeZone.Automatic : setting.Trim();
        TimeZoneInfo zone = HackTimeZone.Resolve(value, _device);
        bool changed = !zone.Equals(TimeZone);
        Setting = value;
        TimeZone = zone;
        if (raiseEvent && changed)
        {
            Changed?.Invoke();
        }
    }
}
