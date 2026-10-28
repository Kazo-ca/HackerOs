using System.Text.Json.Serialization;
using HackerOs.AppSdk.Blazor.Display;
using HackerOs.AppSdk.Globalization;
using HackerOs.Platform.Core.Globalization;
using HackerOs.Platform.Core.Settings;
using HackerOs.Platform.Core.Shell;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace HackerOs.Platform.Blazor.Display;

/// <summary>State of the host's <c>window-management</c> permission (ADR 0178 §4).</summary>
public enum ScreenPermission
{
    /// <summary>Not read yet (the service has not started).</summary>
    Unknown,

    /// <summary>The host has no Window Management API (Firefox, Safari, mobile): the user places screens.</summary>
    Unsupported,

    /// <summary>The host will ask the user at the next new screen.</summary>
    Prompt,

    /// <summary>Screens open directly on a free monitor.</summary>
    Granted,

    /// <summary>The user refused: the user places screens.</summary>
    Denied,
}

/// <summary>How a request to open a secondary screen ended.</summary>
public enum ScreenOpenStatus
{
    /// <summary>The screen is open; <see cref="ScreenOpenResult.Screen"/> is set.</summary>
    Opened,

    /// <summary>The host browser refused the window (pop-up blocker, no user gesture).</summary>
    Blocked,

    /// <summary>This host cannot open windows (no JavaScript runtime, prerendering, tests).</summary>
    Unavailable,

    /// <summary>Every screen number is taken (<see cref="ScreensSettingsDocuments.MaxScreenNumber"/>).</summary>
    LimitReached,
}

/// <summary>Result of <see cref="ScreenService.OpenScreenAsync"/>.</summary>
/// <param name="Status">Whether the screen opened.</param>
/// <param name="Screen">The new screen when <paramref name="Status"/> is <see cref="ScreenOpenStatus.Opened"/>.</param>
public sealed record ScreenOpenResult(ScreenOpenStatus Status, SecondaryScreen? Screen);

/// <summary>One open secondary screen: a display surface registered in the window manager.</summary>
public sealed class SecondaryScreen
{
    internal SecondaryScreen(DisplayId id, IDisplaySurface surface, string? monitorLabel)
    {
        Id = id;
        Surface = surface;
        MonitorLabel = monitorLabel;
        Width = surface.Width;
        Height = surface.Height;
    }

    /// <summary>Gets the screen number (2 or more), its <see cref="DisplayId"/> in the window manager.</summary>
    public DisplayId Id { get; }

    /// <summary>Gets the host window that shows the screen.</summary>
    public IDisplaySurface Surface { get; }

    /// <summary>
    /// Gets the label of the monitor the OS placed the screen on (empty when the browser gives no
    /// name), or <see langword="null"/> when the user places the window (fallback, ADR 0178 §4).
    /// </summary>
    public string? MonitorLabel { get; internal set; }

    /// <summary>Gets whether the screen's window is full screen.</summary>
    public bool IsFullScreen { get; internal set; }

    /// <summary>Gets the last place of the screen's window on the host's desktop, once known.</summary>
    public ScreenGeometry? Geometry { get; internal set; }

    internal int Width { get; set; }

    internal int Height { get; set; }
}

/// <summary>
/// The screen service (ADR 0178 §4, ADR 0180): opens each secondary screen as a display surface,
/// on a free monitor when the host has the Window Management API (Chrome, Edge) and the user allows
/// it, or where the user put it last time otherwise. It registers each screen in
/// <see cref="WindowRuntime"/> (<see cref="AddDisplayCommand"/>, <see cref="ChangeViewportCommand"/>,
/// <see cref="RemoveDisplayCommand"/>), remembers the screens of the session in
/// <c>~/.config/hackeros/screens.config</c> to offer « Restaurer les écrans » after a reload,
/// follows unplugged monitors, and tells the screens when the main tab is hidden.
/// </summary>
/// <remarks>
/// A host browser opens a window only during a user gesture: <see cref="OpenScreenAsync"/> and
/// <see cref="RestoreScreensAsync"/> are called from a click or a key press. One instance per
/// session scope, like <see cref="WindowRuntime"/>.
/// </remarks>
public sealed class ScreenService : IAsyncDisposable
{
    private const string ModulePath = "./_content/HackerOs.Platform.Blazor/screens.js";
    private const int DefaultWidth = 1280;
    private const int DefaultHeight = 800;

    private readonly DisplaySurfaceService _surfaces;
    private readonly IJSRuntime _jsRuntime;
    private readonly WindowRuntime _runtime;
    private readonly IOsSettingsService? _settings;
    private readonly ILogger _logger;
    private readonly Microsoft.Extensions.Localization.IStringLocalizer _text;
    private readonly List<SecondaryScreen> _screens = [];
    private readonly List<DisplayId> _restorable = [];
    private readonly HashSet<DisplayId> _closingOnRequest = [];
    private readonly Dictionary<int, ScreenGeometry> _geometries = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private IJSObjectReference? _module;
    private DotNetObjectReference<ScreenService>? _self;
    private CancellationTokenSource? _pendingGeometrySave;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates the service.</summary>
    /// <param name="surfaces">Opens the screens' host windows.</param>
    /// <param name="jsRuntime">The runtime of the page that hosts the desktop.</param>
    /// <param name="runtime">The window manager the screens are registered in.</param>
    /// <param name="settings">Reads and writes <c>screens.config</c>; without it nothing is remembered.</param>
    /// <param name="logger">Receives the writes that failed.</param>
    /// <param name="locale">The session language of the window titles (ADR 0104); English without it.</param>
    public ScreenService(
        DisplaySurfaceService surfaces,
        IJSRuntime jsRuntime,
        WindowRuntime runtime,
        IOsSettingsService? settings = null,
        ILogger<ScreenService>? logger = null,
        SessionLocaleService? locale = null)
    {
        _surfaces = surfaces ?? throw new ArgumentNullException(nameof(surfaces));
        _jsRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _settings = settings;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _text = Strings.For(() => locale?.Culture ?? HackLocale.DefaultLanguage);
    }

    /// <summary>Raised after a screen opened, closed or changed, and after the offers changed. Raised outside any Blazor event.</summary>
    public event Action? Changed;

    /// <summary>Gets the open secondary screens, by number.</summary>
    public IReadOnlyList<SecondaryScreen> Screens => [.. _screens.OrderBy(screen => screen.Id.Number)];

    /// <summary>Gets the state of the <c>window-management</c> permission.</summary>
    public ScreenPermission Permission { get; private set; }

    /// <summary>Gets whether the main tab is hidden: the browser then slows the whole OS down (ADR 0178 §4).</summary>
    public bool IsPrimaryHidden { get; private set; }

    /// <summary>Gets the screens of the previous session that « Restaurer les écrans » reopens, in order.</summary>
    public IReadOnlyList<DisplayId> RestorableScreens => [.. _restorable];

    /// <summary>Gets whether one more screen can open.</summary>
    public bool CanOpenScreen => _runtime.NextDisplayId.Number <= ScreensSettingsDocuments.MaxScreenNumber;

    /// <summary>Gets or sets how long the service waits after a window moved before writing its place.</summary>
    internal TimeSpan GeometrySaveDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Reads <c>screens.config</c> (screens to restore, remembered places) and starts following the
    /// permission and the main tab. Safe to call more than once.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        await ReadSettingsAsync(cancellationToken);
        try
        {
            IJSObjectReference module = await GetModuleAsync(cancellationToken);
            _self ??= DotNetObjectReference.Create(this);
            StartReply reply = await module.InvokeAsync<StartReply>("start", cancellationToken, _self);
            Permission = ParsePermission(reply.Permission);
            IsPrimaryHidden = reply.Hidden;
        }
        catch (Exception exception) when (exception is InvalidOperationException or JSDisconnectedException or JSException)
        {
            // A host without interactive JavaScript has no screens to follow.
            Permission = ScreenPermission.Unsupported;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Opens a new screen, with the smallest free number (ADR 0178 §5). Call it from a user gesture.
    /// Opening a screen by hand drops the « Restaurer les écrans » offer.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<ScreenOpenResult> OpenScreenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_restorable.Count > 0)
        {
            _restorable.Clear();
            Changed?.Invoke();
        }

        ScreenOpenResult result = await OpenNumberAsync(null, notifyBlocked: true, cancellationToken);
        if (result.Status == ScreenOpenStatus.Opened)
        {
            await SaveOpenScreensAsync(cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// Reopens the screens of the previous session in their order (« Restaurer les écrans »). Call it
    /// from a user gesture. A browser may allow one window per gesture: the screens it blocks stay
    /// offered for the next click.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The number of screens reopened.</returns>
    public async Task<int> RestoreScreensAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int opened = 0;
        foreach (DisplayId id in _restorable.ToArray())
        {
            ScreenOpenResult result = await OpenNumberAsync(id, notifyBlocked: opened == 0, cancellationToken);
            if (result.Status == ScreenOpenStatus.Blocked)
            {
                break;
            }

            _restorable.Remove(id);
            if (result.Status == ScreenOpenStatus.Opened)
            {
                opened++;
            }
            else if (result.Status == ScreenOpenStatus.Unavailable)
            {
                _restorable.Clear();
                break;
            }
        }

        if (opened > 0)
        {
            await SaveOpenScreensAsync(cancellationToken);
        }

        Changed?.Invoke();
        return opened;
    }

    /// <summary>Drops the « Restaurer les écrans » offer: the next reload offers only the screens open then.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task DismissRestoreAsync(CancellationToken cancellationToken = default)
    {
        if (_restorable.Count == 0)
        {
            return;
        }

        _restorable.Clear();
        Changed?.Invoke();
        await SaveOpenScreensAsync(cancellationToken);
    }

    /// <summary>Closes a screen; its windows come back to screen 1 and it is no longer restored.</summary>
    /// <param name="id">The screen to close.</param>
    public async Task CloseScreenAsync(DisplayId id)
    {
        SecondaryScreen? screen = _screens.FirstOrDefault(candidate => candidate.Id == id);
        if (screen is null)
        {
            return;
        }

        _closingOnRequest.Add(id);
        await screen.Surface.CloseAsync();
    }

    /// <summary>
    /// Enters or leaves full screen in a screen's window. Call it from a click in that screen: the
    /// browser asks for a gesture in the window that goes full screen (ADR 0178 §4).
    /// </summary>
    /// <param name="id">The screen.</param>
    /// <returns><see langword="true"/> when the browser accepted.</returns>
    public async Task<bool> ToggleFullScreenAsync(DisplayId id)
    {
        SecondaryScreen? screen = _screens.FirstOrDefault(candidate => candidate.Id == id);
        if (screen is null)
        {
            return false;
        }

        try
        {
            return await (await GetModuleAsync()).InvokeAsync<bool>("toggleFullScreen", screen.Surface.Id);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JSDisconnectedException or JSException)
        {
            return false;
        }
    }

    /// <summary>Called by <c>screens.js</c> when a screen's window moved or changed size. Not part of the public contract.</summary>
    [JSInvokable]
    public void OnScreenGeometry(string surfaceId, int left, int top, int width, int height)
    {
        SecondaryScreen? screen = FindBySurface(surfaceId);
        if (screen is null || width < ScreenGeometry.MinimumWidth || height < ScreenGeometry.MinimumHeight)
        {
            return;
        }

        ScreenGeometry geometry = new(left, top, width, height);
        if (screen.Geometry == geometry)
        {
            return;
        }

        screen.Geometry = geometry;
        _geometries[screen.Id.Number] = geometry;
        ScheduleGeometrySave();
    }

    /// <summary>Called by <c>screens.js</c> when a screen's window was moved onto a free monitor. Not part of the public contract.</summary>
    [JSInvokable]
    public void OnScreenPlaced(string surfaceId, string label)
    {
        if (FindBySurface(surfaceId) is { } screen)
        {
            screen.MonitorLabel = label ?? string.Empty;
            Changed?.Invoke();
        }
    }

    /// <summary>Called by <c>screens.js</c> when a screen's monitor was unplugged. Not part of the public contract.</summary>
    [JSInvokable]
    public Task OnScreenMonitorLost(string surfaceId) =>
        FindBySurface(surfaceId) is { } screen ? CloseScreenAsync(screen.Id) : Task.CompletedTask;

    /// <summary>Called by <c>screens.js</c> when a screen's window entered or left full screen. Not part of the public contract.</summary>
    [JSInvokable]
    public void OnScreenFullScreen(string surfaceId, bool fullScreen)
    {
        if (FindBySurface(surfaceId) is { } screen && screen.IsFullScreen != fullScreen)
        {
            screen.IsFullScreen = fullScreen;
            Changed?.Invoke();
        }
    }

    /// <summary>Called by <c>screens.js</c> when the main tab was hidden or shown. Not part of the public contract.</summary>
    [JSInvokable]
    public void OnPrimaryVisibility(bool hidden)
    {
        if (IsPrimaryHidden != hidden)
        {
            IsPrimaryHidden = hidden;
            Changed?.Invoke();
        }
    }

    /// <summary>Called by <c>screens.js</c> when the <c>window-management</c> permission changed. Not part of the public contract.</summary>
    [JSInvokable]
    public void OnPermission(string state)
    {
        ScreenPermission permission = ParsePermission(state);
        if (Permission != permission)
        {
            Permission = permission;
            Changed?.Invoke();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pendingGeometrySave?.Cancel();
        foreach (SecondaryScreen screen in _screens)
        {
            screen.Surface.Changed -= OnSurfaceChanged;
        }

        try
        {
            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // The page went away first.
        }

        _self?.Dispose();
    }

    private static ScreenPermission ParsePermission(string? state) => state switch
    {
        "granted" => ScreenPermission.Granted,
        "denied" => ScreenPermission.Denied,
        "prompt" => ScreenPermission.Prompt,
        _ => ScreenPermission.Unsupported,
    };

    private async ValueTask<IJSObjectReference> GetModuleAsync(CancellationToken cancellationToken = default) =>
        _module ??= await _jsRuntime.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath);

    private SecondaryScreen? FindBySurface(string surfaceId) =>
        _screens.FirstOrDefault(screen => screen.Surface.Id == surfaceId);

    private async Task<ScreenOpenResult> OpenNumberAsync(DisplayId? requested, bool notifyBlocked, CancellationToken cancellationToken)
    {
        DisplayId id = requested is { IsPrimary: false } wanted && !_runtime.Displays.Contains(wanted)
            ? wanted
            : _runtime.NextDisplayId;
        if (id.Number > ScreensSettingsDocuments.MaxScreenNumber)
        {
            return new ScreenOpenResult(ScreenOpenStatus.LimitReached, null);
        }

        ScreenPlacementRect? remembered = _geometries.TryGetValue(id.Number, out ScreenGeometry geometry)
            ? new ScreenPlacementRect(geometry.Left, geometry.Top, geometry.Width, geometry.Height)
            : null;
        DisplaySurfaceOptions options = new(
            _text["Screens.WindowTitle", id.Number],
            remembered?.Width ?? DefaultWidth,
            remembered?.Height ?? DefaultHeight);
        ScreenSurfaceOpenResult opened = await _surfaces.OpenScreenSurfaceAsync(
            options, new ScreenPlacementRequest(remembered), notifyBlocked, cancellationToken);
        if (opened.Result.Status != DisplaySurfaceOpenStatus.Opened || opened.Result.Surface is not { } surface)
        {
            return new ScreenOpenResult(
                opened.Result.Status == DisplaySurfaceOpenStatus.Blocked ? ScreenOpenStatus.Blocked : ScreenOpenStatus.Unavailable,
                null);
        }

        SecondaryScreen screen = new(id, surface, opened.Monitor);
        _runtime.Apply(new AddDisplayCommand(id, WorkAreaOf(screen.Width, screen.Height)));
        _screens.Add(screen);
        surface.Changed += OnSurfaceChanged;
        Changed?.Invoke();
        return new ScreenOpenResult(ScreenOpenStatus.Opened, screen);
    }

    // Until each screen has its own desktop (chantier 4), the screen's work area is its window.
    private static WindowBounds WorkAreaOf(int width, int height) => new(0, 0, Math.Max(1, width), Math.Max(1, height));

    private void OnSurfaceChanged(IDisplaySurface surface)
    {
        SecondaryScreen? screen = _screens.FirstOrDefault(candidate => ReferenceEquals(candidate.Surface, surface));
        if (screen is null)
        {
            return;
        }

        if (surface.State == DisplaySurfaceState.Open)
        {
            if (surface.Width != screen.Width || surface.Height != screen.Height)
            {
                screen.Width = surface.Width;
                screen.Height = surface.Height;
                _runtime.Apply(new ChangeViewportCommand(WorkAreaOf(screen.Width, screen.Height), screen.Id));
                Changed?.Invoke();
            }

            return;
        }

        // The window ended: its windows come back to screen 1 (ADR 0178 §2).
        surface.Changed -= OnSurfaceChanged;
        _screens.Remove(screen);
        if (_runtime.Displays.Contains(screen.Id))
        {
            _runtime.Apply(new RemoveDisplayCommand(screen.Id));
        }

        // Closed by the user, lost, or closed on request: not restored. Closed by the OS (log out,
        // shut down): kept, like a reload, so the next session offers it.
        bool closedOnRequest = _closingOnRequest.Remove(screen.Id);
        bool forget = closedOnRequest || surface.State is DisplaySurfaceState.ClosedByUser or DisplaySurfaceState.Lost;
        Changed?.Invoke();
        if (forget)
        {
            _ = SaveOpenScreensAsync(CancellationToken.None);
        }
    }

    private async Task ReadSettingsAsync(CancellationToken cancellationToken)
    {
        if (_settings is null)
        {
            return;
        }

        try
        {
            LayeredConfigSnapshot snapshot = await _settings.ReadAsync(ScreensSettingsDocuments.Document, cancellationToken);
            foreach ((int number, ScreenGeometry geometry) in ScreensSettingsDocuments.ReadGeometries(snapshot))
            {
                _geometries[number] = geometry;
            }

            _restorable.Clear();
            _restorable.AddRange(ScreensSettingsDocuments.ReadOpenScreens(snapshot).Select(DisplayId.FromNumber));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "screens: screens.config could not be read; no screen is restored");
        }
    }

    private void ScheduleGeometrySave()
    {
        _pendingGeometrySave?.Cancel();
        CancellationTokenSource pending = new();
        _pendingGeometrySave = pending;
        _ = SaveGeometriesLaterAsync(pending.Token);
    }

    private async Task SaveGeometriesLaterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(GeometrySaveDelay, cancellationToken);
            await WriteAsync(
                [.. _geometries.Keys.Order().Select(GeometryValue)],
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A newer move replaces this write.
        }
    }

    private Task SaveOpenScreensAsync(CancellationToken cancellationToken)
    {
        List<KeyValuePair<string, string>> values =
        [
            new(ScreensSettingsDocuments.OpenQualifiedKey, ScreensSettingsDocuments.FormatOpenScreens(Screens.Select(screen => screen.Id.Number))),
            .. _geometries.Keys.Order().Select(GeometryValue),
        ];
        return WriteAsync(values, cancellationToken);
    }

    private KeyValuePair<string, string> GeometryValue(int number) =>
        new(ScreensSettingsDocuments.GeometryQualifiedKey(number), _geometries[number].ToString());

    private async Task WriteAsync(IReadOnlyList<KeyValuePair<string, string>> values, CancellationToken cancellationToken)
    {
        if (_settings?.CurrentHomeDirectory is null || values.Count == 0 || _disposed)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _settings.SetManyAsync(ScreensSettingsDocuments.Document, values, SettingLayer.User, writer: null, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "screens: screens.config could not be written");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>What <c>screens.js</c> answers to <c>start</c>.</summary>
    private sealed record StartReply(
        [property: JsonPropertyName("permission")] string Permission,
        [property: JsonPropertyName("hidden")] bool Hidden);
}
