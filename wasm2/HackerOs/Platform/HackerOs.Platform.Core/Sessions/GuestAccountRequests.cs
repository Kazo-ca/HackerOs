namespace HackerOs.Platform.Core.Sessions;

/// <summary>
/// "Create my account" asked from inside the guest's session (ADR 0187 section 5): Settings ▸ Users
/// asks here, and the host, which owns the sign-in screens, closes the guest session and shows its
/// own form. An app never sees the new password: the form belongs to the host, like the
/// administrator authentication window.
/// </summary>
public sealed class GuestAccountRequests
{
    /// <summary>Raised when the guest asked to create their account; the host handles it.</summary>
    public event Action? CreateAccountRequested;

    /// <summary>Gets whether a host listens (a headless host has no form to show).</summary>
    public bool HasListener => CreateAccountRequested is not null;

    /// <summary>Asks the host to show the "Create my account" form.</summary>
    /// <returns><see langword="true"/> when a host received the request.</returns>
    public bool RequestCreateAccount()
    {
        Action? handler = CreateAccountRequested;
        handler?.Invoke();
        return handler is not null;
    }
}
