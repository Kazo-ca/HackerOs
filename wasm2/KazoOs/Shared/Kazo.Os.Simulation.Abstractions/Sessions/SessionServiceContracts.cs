namespace Kazo.Os.Simulation.Abstractions.Sessions;

/// <summary>
/// Defines the local session lifecycle state machine from ADR 0013:
/// <c>Uninitialized -&gt; LoggedOut -&gt; Starting -&gt; Active -&gt; LoggingOut -&gt; LoggedOut</c>,
/// with an <c>Active -&gt; ShuttingDown -&gt; Stopped</c> terminal branch.
/// </summary>
public enum SessionState
{
    /// <summary>The session host has not run its first login yet.</summary>
    Uninitialized,

    /// <summary>No user is authenticated; a new login may begin.</summary>
    LoggedOut,

    /// <summary>A login is validating credentials and provisioning the user's home.</summary>
    Starting,

    /// <summary>A user is authenticated and the session root token is live.</summary>
    Active,

    /// <summary>The session root token has been requested to cancel and cleanup is in progress.</summary>
    LoggingOut,

    /// <summary>The platform is shutting down and cleanup is in progress.</summary>
    ShuttingDown,

    /// <summary>The platform has shut down; no further login is possible.</summary>
    Stopped
}

/// <summary>
/// Owns the local session lifecycle state machine, the session's root cancellation source, and
/// the current authenticated principal, per ADR 0013.
/// </summary>
public interface ISessionService
{
    /// <summary>Gets the current session lifecycle state.</summary>
    SessionState State { get; }

    /// <summary>Gets the current authenticated principal, or <see langword="null"/> when no session is active.</summary>
    AuthenticatedPrincipal? CurrentPrincipal { get; }

    /// <summary>
    /// Authenticates one local user and activates a new session. Only valid from
    /// <see cref="SessionState.Uninitialized"/> or <see cref="SessionState.LoggedOut"/>.
    /// </summary>
    /// <param name="loginName">Login name of the user to authenticate.</param>
    /// <param name="password">Password to verify, or <see langword="null"/> for a user with no credential.</param>
    /// <param name="cancellationToken">Token that cancels home provisioning.</param>
    /// <returns>The newly authenticated principal.</returns>
    /// <exception cref="InvalidOperationException">The session is not in a state that can start a login, the user does not exist, is disabled, or the credential does not match.</exception>
    Task<AuthenticatedPrincipal> LoginAsync(
        LocalLoginName loginName, string? password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the session root token, returning the session to <see cref="SessionState.LoggedOut"/>
    /// so a new login may begin. Only valid from <see cref="SessionState.Active"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session is not <see cref="SessionState.Active"/>.</exception>
    Task LogoutAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the session root token (if any) and transitions to the terminal
    /// <see cref="SessionState.Stopped"/> state. No further login is possible afterward.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session has already stopped, or is mid-transition.</exception>
    Task ShutdownAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new cancellation source linked to the session's root token, for exclusive
    /// ownership by one process/command/window/service lifecycle record. Cancelling the returned
    /// source never cancels the session or any other linked source; cancelling the session cancels
    /// every linked source. Only valid while <see cref="State"/> is <see cref="SessionState.Active"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session is not <see cref="SessionState.Active"/>.</exception>
    CancellationTokenSource CreateLinkedCancellationSource();
}

/// <summary>Published when a session becomes active after a successful login.</summary>
public sealed record SessionActivatedEvent(SessionId SessionId, LocalUserId UserId, DateTimeOffset AtUtc);

/// <summary>Published after a session returns to <see cref="SessionState.LoggedOut"/> via logout.</summary>
public sealed record SessionLoggedOutEvent(SessionId SessionId, LocalUserId UserId, DateTimeOffset AtUtc);

/// <summary>Published after a session reaches the terminal <see cref="SessionState.Stopped"/> state.</summary>
public sealed record SessionShutDownEvent(SessionId? SessionId, DateTimeOffset AtUtc);
