using System.Text.Json.Serialization;
using HackerOs.AppSdk.Blazor.Display;
using HackerOs.AppSdk.Globalization;
using HackerOs.Platform.Core.Globalization;
using HackerOs.Simulation.Abstractions.Notifications;
using HackerOs.Simulation.Abstractions.Sessions;
using HackerOs.Simulation.Abstractions.Time;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HackerOs.Platform.Blazor.Display;

/// <summary>
/// Browser-backed <see cref="IDisplaySurfaceService"/> (ADR 0078 §2): every detached surface is a
/// same-origin window of the host browser, opened with <c>window.open</c> on an empty document and
/// driven by this instance. The JS module (<c>wwwroot/displaySurfaces.js</c>) builds the window's
/// document, mirrors the desktop's style sheets into it and relays its events; every decision
/// (state, notifications, ownership) is made here.
/// </summary>
/// <remarks>
/// The window runs no .NET runtime and no second OS. The Blazor content shown in it is rendered by
/// this instance and moved there by <see cref="DisplaySurfaceContent"/>; its events reach the
/// renderer because <c>HackerOs.Platform.Blazor.lib.module.js</c>, a JS initializer that runs
/// before Blazor starts, mirrors the document-level listeners Blazor delegates events with onto
/// each surface's document. One instance per session scope (circuit on the server host).
/// </remarks>
public sealed class DisplaySurfaceService : IDisplaySurfaceService, IAsyncDisposable
{
    /// <summary>Source app ID of the notifications this service posts.</summary>
    public const string NotificationSource = "org.hackeros.display";

    private const string ModulePath = "./_content/HackerOs.Platform.Blazor/displaySurfaces.js";

    private readonly IJSRuntime _jsRuntime;
    private readonly INotificationQueue? _notifications;
    private readonly ISessionService? _session;
    private readonly ISimulationClock? _clock;
    private readonly List<Surface> _surfaces = [];
    private IJSObjectReference? _module;
    private DotNetObjectReference<DisplaySurfaceService>? _self;
    private readonly Microsoft.Extensions.Localization.IStringLocalizer _text;
    private int _nextId;
    private bool _disposed;

    /// <summary>Creates the service over the session's JavaScript runtime.</summary>
    /// <param name="jsRuntime">The runtime of the page that hosts the desktop.</param>
    /// <param name="notifications">Where a blocked window is reported; optional.</param>
    /// <param name="session">Gives the user a notification is posted to; optional.</param>
    /// <param name="clock">Stamps notifications; optional.</param>
    /// <param name="locale">The session language of the notification (ADR 0104); optional, English without it.</param>
    public DisplaySurfaceService(
        IJSRuntime jsRuntime,
        INotificationQueue? notifications = null,
        ISessionService? session = null,
        ISimulationClock? clock = null,
        SessionLocaleService? locale = null)
    {
        _jsRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
        _notifications = notifications;
        _session = session;
        _clock = clock;
        _text = Strings.For(() => locale?.Culture ?? HackLocale.DefaultLanguage);
    }

    /// <inheritdoc />
    public IReadOnlyList<IDisplaySurface> Surfaces => _surfaces.Where(surface => surface.State == DisplaySurfaceState.Open).ToArray();

    /// <inheritdoc />
    public event Action? SurfacesChanged;

    /// <inheritdoc />
    public async Task<DisplaySurfaceOpenResult> OpenAsync(DisplaySurfaceOptions options, CancellationToken cancellationToken = default) =>
        (await OpenCoreAsync(options, placement: null, notifyBlocked: true, cancellationToken)).Result;

    /// <summary>
    /// Opens the surface of a secondary screen (ADR 0178 §4): like <see cref="OpenAsync"/>, but
    /// <c>screens.js</c> places the window on a free monitor when the host allows it, or where
    /// <paramref name="placement"/> remembers it, and follows it afterwards. Used by
    /// <see cref="ScreenService"/>.
    /// </summary>
    /// <param name="options">Title and size used when no placement applies.</param>
    /// <param name="placement">Where the screen's window was last time; its geometry may be empty.</param>
    /// <param name="notifyBlocked">Whether a blocked window posts the notification.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    internal Task<ScreenSurfaceOpenResult> OpenScreenSurfaceAsync(
        DisplaySurfaceOptions options,
        ScreenPlacementRequest placement,
        bool notifyBlocked,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return OpenCoreAsync(options, placement, notifyBlocked, cancellationToken);
    }

    private async Task<ScreenSurfaceOpenResult> OpenCoreAsync(
        DisplaySurfaceOptions options,
        ScreenPlacementRequest? placement,
        bool notifyBlocked,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string id = $"surface-{Interlocked.Increment(ref _nextId)}";
        SurfaceOpenReply reply;
        try
        {
            IJSObjectReference module = await GetModuleAsync(cancellationToken);
            _self ??= DotNetObjectReference.Create(this);
            int width = Math.Max(200, options.Width);
            int height = Math.Max(150, options.Height);
            reply = placement is null
                ? await module.InvokeAsync<SurfaceOpenReply>("open", cancellationToken, _self, id, options.Title, width, height)
                : await module.InvokeAsync<SurfaceOpenReply>("open", cancellationToken, _self, id, options.Title, width, height, placement);
        }
        catch (InvalidOperationException)
        {
            // Prerendering or a host without interactive JavaScript: no window can be opened.
            return new ScreenSurfaceOpenResult(DisplaySurfaceOpenResult.Unavailable, null);
        }
        catch (JSDisconnectedException)
        {
            return new ScreenSurfaceOpenResult(DisplaySurfaceOpenResult.Unavailable, null);
        }

        if (reply.Status != "opened")
        {
            if (notifyBlocked)
            {
                PostBlockedNotification(options.Title);
            }

            return new ScreenSurfaceOpenResult(new DisplaySurfaceOpenResult(DisplaySurfaceOpenStatus.Blocked, null), null);
        }

        Surface surface = new(this, id, options.Title, reply.Width, reply.Height);
        _surfaces.Add(surface);
        SurfacesChanged?.Invoke();
        return new ScreenSurfaceOpenResult(new DisplaySurfaceOpenResult(DisplaySurfaceOpenStatus.Opened, surface), reply.Monitor);
    }

    /// <inheritdoc />
    public async Task CloseAllAsync()
    {
        foreach (Surface surface in _surfaces.ToArray())
        {
            await surface.CloseAsync();
        }
    }

    /// <summary>
    /// Called by the module when a surface's window changed: <c>resize</c>, <c>focus</c>,
    /// <c>blur</c>, <c>closed</c> (the user closed it) or <c>lost</c> (it left the surface's
    /// document). Not part of the public contract.
    /// </summary>
    [JSInvokable]
    public void OnSurfaceEvent(string id, string type, int width, int height)
    {
        Surface? surface = _surfaces.FirstOrDefault(candidate => candidate.Id == id);
        if (surface is null)
        {
            return;
        }

        switch (type)
        {
            case "resize":
                surface.Update(width, height, surface.HasFocus);
                break;
            case "focus":
                surface.Update(surface.Width, surface.Height, hasFocus: true);
                break;
            case "blur":
                surface.Update(surface.Width, surface.Height, hasFocus: false);
                break;
            case "closed":
                End(surface, DisplaySurfaceState.ClosedByUser);
                break;
            case "lost":
                End(surface, DisplaySurfaceState.Lost);
                break;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await CloseAllAsync();
            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // The page went away first; its windows went with it.
        }

        _disposed = true;
        _self?.Dispose();
    }

    private async ValueTask<IJSObjectReference> GetModuleAsync(CancellationToken cancellationToken = default) =>
        _module ??= await _jsRuntime.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath);

    private void End(Surface surface, DisplaySurfaceState state)
    {
        if (surface.State != DisplaySurfaceState.Open)
        {
            return;
        }

        _surfaces.Remove(surface);
        surface.End(state);
        SurfacesChanged?.Invoke();
    }

    private void PostBlockedNotification(string title)
    {
        if (_notifications is null || _session?.CurrentPrincipal is not { } principal)
        {
            return;
        }

        DateTimeOffset now = _clock?.UtcNow ?? DateTimeOffset.UtcNow;
        _notifications.Enqueue(new Notification(
            NotificationId.FromGuid(Guid.NewGuid()),
            principal.UserId,
            NotificationSource,
            NotificationSeverity.Warning,
            _text["Display.Blocked.Title"],
            _text["Display.Blocked.Message", title],
            [],
            now,
            now + TimeSpan.FromMinutes(5)));
    }

    /// <summary>What the module answers to <c>open</c>.</summary>
    private sealed record SurfaceOpenReply(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("width")] int Width,
        [property: JsonPropertyName("height")] int Height,
        [property: JsonPropertyName("monitor")] string? Monitor = null);

    /// <summary>One detached surface, driven through the service's module.</summary>
    private sealed class Surface(DisplaySurfaceService owner, string id, string title, int width, int height) : IDisplaySurface
    {
        public string Id { get; } = id;

        public string Title { get; private set; } = title;

        public int Width { get; private set; } = width;

        public int Height { get; private set; } = height;

        public bool HasFocus { get; private set; } = true;

        public DisplaySurfaceState State { get; private set; } = DisplaySurfaceState.Open;

        public event Action<IDisplaySurface>? Changed;

        public async ValueTask SetTitleAsync(string newTitle)
        {
            ArgumentNullException.ThrowIfNull(newTitle);
            if (State != DisplaySurfaceState.Open || newTitle == Title)
            {
                return;
            }

            Title = newTitle;
            await (await owner.GetModuleAsync()).InvokeVoidAsync("setTitle", Id, newTitle);
            Changed?.Invoke(this);
        }

        public async ValueTask FocusAsync()
        {
            if (State == DisplaySurfaceState.Open)
            {
                await (await owner.GetModuleAsync()).InvokeVoidAsync("focus", Id);
            }
        }

        public async ValueTask CloseAsync()
        {
            if (State != DisplaySurfaceState.Open)
            {
                return;
            }

            // The state changes first: the window's own "closed" event, if any, finds it ended.
            owner.End(this, DisplaySurfaceState.Closed);
            try
            {
                await (await owner.GetModuleAsync()).InvokeVoidAsync("close", Id);
            }
            catch (JSDisconnectedException)
            {
                // The page is gone: so are its windows.
            }
        }

        public async ValueTask MountAsync(ElementReference content)
        {
            if (State == DisplaySurfaceState.Open)
            {
                await (await owner.GetModuleAsync()).InvokeVoidAsync("mount", Id, content);
            }
        }

        public async ValueTask UnmountAsync(ElementReference content) =>
            await (await owner.GetModuleAsync()).InvokeVoidAsync("unmount", Id, content);

        internal void Update(int newWidth, int newHeight, bool hasFocus)
        {
            if (newWidth == Width && newHeight == Height && hasFocus == HasFocus)
            {
                return;
            }

            Width = newWidth;
            Height = newHeight;
            HasFocus = hasFocus;
            Changed?.Invoke(this);
        }

        internal void End(DisplaySurfaceState state)
        {
            State = state;
            HasFocus = false;
            Changed?.Invoke(this);
        }
    }
}

/// <summary>Where a secondary screen's window goes (ADR 0178 §4); sent to <c>screens.js</c>.</summary>
/// <param name="Remembered">Where the screen's window was last time, or <see langword="null"/>.</param>
internal sealed record ScreenPlacementRequest(
    [property: JsonPropertyName("remembered")] ScreenPlacementRect? Remembered);

/// <summary>A window placement in CSS pixels of the host's virtual desktop.</summary>
internal sealed record ScreenPlacementRect(
    [property: JsonPropertyName("left")] int Left,
    [property: JsonPropertyName("top")] int Top,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

/// <summary>How opening a screen's surface ended.</summary>
/// <param name="Result">The surface result.</param>
/// <param name="Monitor">Label of the monitor the window was placed on (may be empty), or <see langword="null"/> when the user places it.</param>
internal sealed record ScreenSurfaceOpenResult(DisplaySurfaceOpenResult Result, string? Monitor);
