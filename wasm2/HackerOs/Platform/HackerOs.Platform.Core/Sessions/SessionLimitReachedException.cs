namespace HackerOs.Platform.Core.Sessions;

/// <summary>
/// Thrown when a sign-in would open one session more than the page allows (ADR 0201 section 6).
/// Resuming a session that is already open is never refused.
/// </summary>
public sealed class SessionLimitReachedException : InvalidOperationException
{
    /// <summary>Initializes the exception for the given limit.</summary>
    /// <param name="maxOpenSessions">How many sessions may be open at once.</param>
    public SessionLimitReachedException(int maxOpenSessions)
        : base($"Too many open sessions: at most {maxOpenSessions} may be open at once.")
    {
        MaxOpenSessions = maxOpenSessions;
    }

    /// <summary>Gets how many sessions may be open at once.</summary>
    public int MaxOpenSessions { get; }
}
