using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.Platform.Core.Lifecycle;
using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Simulation.Abstractions.Sessions;
using HackerOs.Windowing.Abstractions;

namespace HackerOs.Platform.Blazor.Shell;

/// <summary>
/// Which windows the desktop shows when several sessions are open (ADR 0201 section 3): a window
/// belongs to the session of its app instance, and only those of the foreground session are shown,
/// listed by the taskbar and offered by the window switcher. The others stay rendered, hidden.
/// </summary>
public sealed class SessionWindowFilter
{
    private readonly ISessionService _sessions;
    private readonly AppLifecycleOrchestrator _orchestrator;

    /// <summary>Initializes the filter.</summary>
    /// <param name="sessions">Gives the foreground session.</param>
    /// <param name="orchestrator">Gives the session of each running app instance.</param>
    public SessionWindowFilter(ISessionService sessions, AppLifecycleOrchestrator orchestrator)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
    }

    /// <summary>
    /// Gets whether the window belongs to the foreground session. None is shown while no session is in
    /// the foreground (the "Switch user" screen); a window with no running app instance (shell
    /// content) is always shown.
    /// </summary>
    /// <param name="window">A window of the window runtime.</param>
    public bool Shows(WindowRuntimeState window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_sessions.CurrentPrincipal is not { } principal)
        {
            return false;
        }

        return !_orchestrator.TryGetRunningInstance(
                AppInstanceId.FromGuid(window.OwnerInstanceId.Value), out _, out IAppExecutionContext? context)
            || context.SessionId == principal.SessionId;
    }
}
