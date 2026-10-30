namespace HackerOs.Simulation.Abstractions.Sessions;

/// <summary>
/// "Switch user" (ADR 0201): several sessions stay open in the same page, one in the foreground.
/// Implemented next to <see cref="ISessionService"/>, whose <see cref="ISessionService.CurrentPrincipal"/>
/// is the foreground session; the sessions in the background keep their processes, services and
/// windows, as on a real desktop where a switched-away session keeps running.
/// </summary>
public interface IUserSwitchingService
{
    /// <summary>Gets every open session, the foreground one included, in the order they were opened.</summary>
    IReadOnlyList<AuthenticatedPrincipal> OpenSessions { get; }

    /// <summary>Gets the open sessions that are not in the foreground.</summary>
    IReadOnlyList<AuthenticatedPrincipal> BackgroundSessions { get; }

    /// <summary>Gets how many sessions may be open at once (one WebAssembly tab holds them all).</summary>
    int MaxOpenSessions { get; }

    /// <summary>
    /// Moves the foreground session to the background: its root token stays live, and the state
    /// returns to <see cref="SessionState.LoggedOut"/> so a sign-in screen may open. Signing in again
    /// to the same account resumes it.
    /// </summary>
    /// <exception cref="InvalidOperationException">No session is in the foreground.</exception>
    Task SwitchAwayAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends one session in the background (<c>loginctl terminate-session</c>): its root token is
    /// cancelled and <see cref="SessionLoggedOutEvent"/> is published. The caller stops its apps first.
    /// </summary>
    /// <param name="sessionId">A session listed by <see cref="BackgroundSessions"/>.</param>
    /// <param name="cancellationToken">Not used by the in-memory implementation.</param>
    /// <exception cref="InvalidOperationException">No such session in the background.</exception>
    Task EndBackgroundSessionAsync(SessionId sessionId, CancellationToken cancellationToken = default);
}

/// <summary>Published when the foreground session moves to the background (ADR 0201).</summary>
public sealed record SessionSwitchedOutEvent(SessionId SessionId, LocalUserId UserId, DateTimeOffset AtUtc);

/// <summary>Published when a session in the background comes back to the foreground (ADR 0201).</summary>
public sealed record SessionResumedEvent(SessionId SessionId, LocalUserId UserId, DateTimeOffset AtUtc);
