using Kazo.Os.App.Abstractions;
using Kazo.Os.MobileShell.Blazor;
using Kazo.Os.Platform.Blazor.Windows;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Windowing.Abstractions;
using Kazo.Os.Windowing.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kazo.Os.Platform.Blazor.Shell;

/// <summary>Single-surface mobile shell with Home, application drawer and system navigation gestures.</summary>
public partial class MobileShell
{
    [Inject] private ISessionService MobileSession { get; set; } = null!;
    [Inject] private WindowLaunchCoordinator MobileLaunchCoordinator { get; set; } = null!;
    [Inject] private IEventBus MobileEvents { get; set; } = null!;
    [Inject] private IProcessManager MobileProcesses { get; set; } = null!;
    private IDisposable? _mobileProcessObserver;
    private ElementReference _shellRoot;
    private IMobileNavigationCommands _systemNavigationCommands = null!;
    private bool _isDrawerOpen;
    private bool _mobileLaunching;
    private string? _mobileLaunchError;
    private bool HasActiveMobileWindow => SingleSurfacePresentationPolicy.SelectPrimary(WindowRuntime.Windows) is not null;

    private Kazo.Os.Platform.Blazor.Navigation.HardwareNavigationService? _hardwareNavigation;
    private Func<Kazo.Os.Platform.Blazor.Navigation.HardwareNavigationRequest, Task>? _hardwareNavigationRoute;

    /// <summary>ADR 0220 §2: Back runs the ◁ sequence with the gesture's source; Forward goes to the foreground app.</summary>
    private async Task RouteHardwareNavigationAsync(Kazo.Os.Platform.Blazor.Navigation.HardwareNavigationRequest request)
    {
        if (request.Direction == Kazo.Os.Platform.Blazor.Navigation.HardwareNavigationDirection.Forward)
        {
            await _navigationCommands.RequestForwardAsync(request.Source);
        }
        else
        {
            await _navigationCommands.RequestBackAsync(request.Source);
        }

        StateHasChanged();
    }

    private void InitializeMobileHome()
    {
        _mobileProcessObserver = new WindowProcessObserver(MobileEvents, MobileProcesses, Orchestrator,
            WindowRuntime, MobileLaunchCoordinator, work => InvokeAsync(work));
        _systemNavigationCommands = new NavigationActions(
        () => _navigationCommands.RequestBack(),
        () => { CloseOverlays(); _navigationCommands.RequestHome(); },
        () => { _isDrawerOpen = false; _navigationCommands.RequestRecent(); });
    }

    private Task SetDrawerAsync(bool open)
    {
        if (open)
        {
            _navigationCommands.RequestHome();
            _isShadeOpen = false;
            _showRecent = false;
        }
        _isDrawerOpen = open;
        StateHasChanged();
        return Task.CompletedTask;
    }

    /// <summary>Interprets completed shade and navigation swipes for touch, pen or mouse.</summary>
    [JSInvokable]
    public async Task ReportShellGestureAsync(string surface, double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || Math.Abs(dy) <= Math.Abs(dx)) return;
        if (surface == "shade" && dy >= 32) OpenShade();
        else if (surface == "drawer" && dy <= -48) await SetDrawerAsync(true);
    }

    /// <summary>Keeps the simulated work area aligned with the mobile workspace and orientation.</summary>
    [JSInvokable]
    public void ReportMobileWorkspaceSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return;
        if (WindowRuntime.WorkArea.Width != width || WindowRuntime.WorkArea.Height != height)
            WindowRuntime.Apply(new ChangeViewportCommand(new WindowBounds(0, 0, width, height)));
    }

    private async Task LaunchMobileAppAsync(AppManifest app)
    {
        if (_mobileLaunching || MobileSession.CurrentPrincipal is not AuthenticatedPrincipal principal) return;
        _mobileLaunching = true;
        _mobileLaunchError = null;
        try
        {
            // Mobile icons return to an existing app process instead of creating a new window on every tap.
            WindowRuntimeState? existing = WindowRuntime.Windows.LastOrDefault(window => window.AppId == app.Id);
            if (existing is not null) { ActivateRecent(existing.Id); return; }
            AppLaunchResult result = await Orchestrator.LaunchAsync(new AppLaunchRequest(app.Id, principal, []));
            if (result.IsSuccess) await MobileLaunchCoordinator.PresentAsync(app, result);
            else _mobileLaunchError = result.ErrorMessage ?? L["Shell.LaunchFailed"];
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _mobileLaunchError = L["Shell.LaunchFailed"];
        }
        finally { _mobileLaunching = false; }
    }

    private sealed class NavigationActions(Action back, Action home, Action recent) : IMobileNavigationCommands
    {
        public void RequestBack() => back();
        public void RequestHome() => home();
        public void RequestRecent() => recent();
    }
}
