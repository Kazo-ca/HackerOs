namespace Kazo.Os.AppSdk.Shell;

/// <summary>
/// The questions a hosted shell asks its terminal (ADR 0056, section 7): <c>sudo</c>'s
/// administrator credentials, the one-shot approval offered after a permission denial (ADR 0042)
/// and the text-mode approval of a command's dangerous capabilities (ADR 0049, section 5).
/// </summary>
/// <remarks>
/// <para>
/// The terminal only <b>reads</b>: it shows the question and returns what the user typed. The OS
/// verifies the credentials, mints the ticket, writes <c>/etc/hackeros/app-permissions.config</c>
/// and <c>/var/log/auth.log</c>; the terminal never decides anything, as a real terminal does not
/// check <c>sudo</c>'s password itself.
/// </para>
/// <para>
/// Every member may be called from a command's flow while a line runs. Only
/// <see cref="ReadCredentialAsync"/> is required; a prompt that cannot ask a yes/no question
/// leaves <see cref="ConfirmAsync"/> as is, and the dangerous-capability approval then ends with
/// no decision (asked again at the next launch).
/// </para>
/// </remarks>
public abstract class KazoCredentialPrompt
{
    /// <summary>
    /// Asks for an administrator's login name and password, as <c>sudo</c> does: the input must be
    /// masked.
    /// </summary>
    /// <param name="request">What the credentials are for.</param>
    /// <param name="cancellationToken">Cancelled with the line (Ctrl+C) or the shell.</param>
    /// <returns>The credentials typed, or <see langword="null"/> when the user cancelled.</returns>
    public abstract Task<KazoCredential?> ReadCredentialAsync(
        KazoCredentialRequest request, CancellationToken cancellationToken = default);

    /// <summary>Asks a yes / no / cancel question (the dangerous-capability approval's first step).</summary>
    /// <param name="request">Title and question.</param>
    /// <param name="cancellationToken">Cancelled with the line (Ctrl+C) or the shell.</param>
    /// <returns>The answer; <see cref="KazoConfirmation.Cancel"/> by default.</returns>
    public virtual Task<KazoConfirmation> ConfirmAsync(
        KazoConfirmationRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(KazoConfirmation.Cancel);

    /// <summary>
    /// Prints one line of the OS's own messages in the terminal (approval banner, "Sorry, try
    /// again."). Ignored by default.
    /// </summary>
    /// <param name="text">The line, without its terminator.</param>
    /// <param name="kind">How to show it.</param>
    /// <param name="cancellationToken">Cancelled with the line (Ctrl+C) or the shell.</param>
    public virtual Task WriteLineAsync(
        string text, KazoPromptLineKind kind, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>What a credential request is for.</summary>
/// <param name="Title">Short title, e.g. "Administrator Authorization Required".</param>
/// <param name="Description">The operation in plain words, e.g. the command <c>sudo</c> will run.</param>
public sealed record KazoCredentialRequest(string Title, string Description);

/// <summary>Credentials typed in a terminal, handed straight to the OS for verification.</summary>
/// <param name="LoginName">Administrator login name.</param>
/// <param name="Password">Password as typed.</param>
public sealed record KazoCredential(string LoginName, string Password)
{
    /// <summary>Never prints the password (logs, debugger displays).</summary>
    public override string ToString() => $"KazoCredential {{ LoginName = {LoginName}, Password = *** }}";
}

/// <summary>A yes / no / cancel question.</summary>
/// <param name="Title">Short title.</param>
/// <param name="Message">The question, possibly on several lines.</param>
public sealed record KazoConfirmationRequest(string Title, string Message);

/// <summary>Answer to a <see cref="KazoConfirmationRequest"/>.</summary>
public enum KazoConfirmation
{
    /// <summary>Accept.</summary>
    Yes,

    /// <summary>Refuse.</summary>
    No,

    /// <summary>Do not decide now.</summary>
    Cancel
}

/// <summary>How a terminal shows a line written by <see cref="KazoCredentialPrompt.WriteLineAsync"/>.</summary>
public enum KazoPromptLineKind
{
    /// <summary>Warning banner, capability list.</summary>
    Warning,

    /// <summary>Neutral information.</summary>
    Info,

    /// <summary>Failure (wrong password, request cancelled).</summary>
    Error
}
