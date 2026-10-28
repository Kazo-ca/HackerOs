using HackerOs.AppSdk.Administration;
using HackerOs.Platform.Core.Administration;
using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Core;

namespace HackerOs.Platform.Blazor.Display;

/// <summary>
/// The browser host's <see cref="IScreenControl"/> (ADR 0184): <c>HackScreens</c> over the screen
/// service and the window manager. One instance per session scope, like <see cref="ScreenService"/>.
/// </summary>
public sealed class ScreenControl : IScreenControl, IDisposable
{
    private readonly ScreenService _screens;
    private readonly WindowRuntime _runtime;

    /// <summary>Creates the adapter.</summary>
    /// <param name="screens">The session's screens.</param>
    /// <param name="runtime">The window manager, which knows the screen of each window.</param>
    public ScreenControl(ScreenService screens, WindowRuntime runtime)
    {
        _screens = screens ?? throw new ArgumentNullException(nameof(screens));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _screens.Changed += OnChanged;
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public bool IsAvailable => _screens.IsStarted;

    /// <inheritdoc />
    public HackScreensState GetState(Guid appInstanceId)
    {
        WindowBounds primary = _runtime.GetWorkArea(DisplayId.Primary);
        List<HackScreenInfo> screens = [new(1, null, (int)primary.Width, (int)primary.Height, _screens.IsPrimaryFullScreen)];
        foreach (SecondaryScreen screen in _screens.Screens)
        {
            WindowBounds area = _runtime.GetWorkArea(screen.Id);
            screens.Add(new HackScreenInfo(
                screen.Id.Number,
                string.IsNullOrWhiteSpace(screen.MonitorLabel) ? null : screen.MonitorLabel,
                (int)area.Width,
                (int)area.Height,
                screen.IsFullScreen));
        }

        return new HackScreensState(
            screens,
            (HackScreenPermission)_screens.Permission,
            _screens.CanOpenScreen,
            [.. _screens.RestorableScreens.Select(static id => id.Number)],
            CallerDisplay(appInstanceId).Number);
    }

    /// <inheritdoc />
    public async Task<HackScreenOpenStatus> OpenScreenAsync(CancellationToken cancellationToken) =>
        (HackScreenOpenStatus)(await _screens.OpenScreenAsync(cancellationToken)).Status;

    /// <inheritdoc />
    public Task<int> RestoreScreensAsync(CancellationToken cancellationToken) => _screens.RestoreScreensAsync(cancellationToken);

    /// <inheritdoc />
    public Task CloseScreenAsync(int number) =>
        number >= 2 ? _screens.CloseScreenAsync(DisplayId.FromNumber(number)) : Task.CompletedTask;

    /// <inheritdoc />
    public Task IdentifyAsync(CancellationToken cancellationToken)
    {
        // The numbers stay lit after the call returns: the app's click handler is not held for 3 seconds.
        _ = _screens.IdentifyAsync(CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> ToggleFullScreenAsync(Guid appInstanceId)
    {
        DisplayId display = CallerDisplay(appInstanceId);
        return display.IsPrimary ? _screens.TogglePrimaryFullScreenAsync() : _screens.ToggleFullScreenAsync(display);
    }

    /// <inheritdoc />
    public async Task<HackScreenPermission> RequestPermissionAsync() =>
        (HackScreenPermission)await _screens.RequestPermissionAsync();

    /// <inheritdoc />
    public void Dispose() => _screens.Changed -= OnChanged;

    /// <summary>The screen of the caller's focused window, else of its first window, else screen 1.</summary>
    private DisplayId CallerDisplay(Guid appInstanceId)
    {
        if (appInstanceId == Guid.Empty)
        {
            return DisplayId.Primary;
        }

        WindowOwnerId owner = WindowOwnerId.FromGuid(appInstanceId);
        List<WindowRuntimeState> windows = [.. _runtime.Windows.Where(window => window.OwnerInstanceId == owner)];
        WindowRuntimeState? window = windows.FirstOrDefault(static candidate => candidate.IsFocused) ?? windows.FirstOrDefault();
        return window?.DisplayId ?? DisplayId.Primary;
    }

    private void OnChanged() => Changed?.Invoke();
}
