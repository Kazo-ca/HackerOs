using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Shell;

namespace Kazo.Os.AppSdk.Terminal;

/// <summary>
/// The terminal side of a hosted shell's questions (ADR 0056, section 7): <c>sudo</c>'s
/// credentials, the one-shot approval after a permission denial (ADR 0042) and the text-mode
/// approval of a command's dangerous capabilities (ADR 0049, section 5). The OS writes its banner
/// in the terminal; the masked input and the yes / no / cancel choice use the platform's dialogs.
/// </summary>
/// <remarks>
/// This class only reads what the user types and hands it back to the shell: verification,
/// tickets, <c>/etc/hackeros/app-permissions.config</c> and <c>/var/log/auth.log</c> belong to
/// the OS.
/// </remarks>
public sealed class TerminalCredentialPrompt : KazoCredentialPrompt
{
    private readonly IDialogService _dialogs;
    private readonly IAppExecutionContext _context;
    private readonly Func<string, KazoPromptLineKind, Task> _writeLine;

    /// <summary>Creates the prompt for one terminal.</summary>
    /// <param name="dialogs">Dialog service used for the masked credential input and the choice.</param>
    /// <param name="context">Execution context of the terminal app (owner of the dialogs).</param>
    /// <param name="writeLine">Appends one line to the terminal output.</param>
    public TerminalCredentialPrompt(
        IDialogService dialogs,
        IAppExecutionContext context,
        Func<string, KazoPromptLineKind, Task> writeLine)
    {
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _writeLine = writeLine ?? throw new ArgumentNullException(nameof(writeLine));
    }

    /// <inheritdoc />
    public override async Task<KazoCredential?> ReadCredentialAsync(
        KazoCredentialRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CredentialPromptDialogResult result = await _dialogs.CredentialPromptAsync(_context, new CredentialPromptDialogRequest
        {
            Title = request.Title,
            Description = request.Description
        }, cancellationToken);

        return result.Status == CredentialPromptStatus.Submitted
            ? new KazoCredential(result.LoginName ?? string.Empty, result.Password ?? string.Empty)
            : null;
    }

    /// <inheritdoc />
    public override async Task<KazoConfirmation> ConfirmAsync(
        KazoConfirmationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        MessageBoxDialogResult choice = await _dialogs.MessageBoxAsync(_context, new MessageBoxDialogRequest
        {
            Title = request.Title,
            Content = request.Message,
            DialogType = MessageBoxType.YesNoCancel
        }, cancellationToken);

        return choice.Result switch
        {
            MessageBoxResult.Yes => KazoConfirmation.Yes,
            MessageBoxResult.No => KazoConfirmation.No,
            _ => KazoConfirmation.Cancel
        };
    }

    /// <inheritdoc />
    public override Task WriteLineAsync(string text, KazoPromptLineKind kind, CancellationToken cancellationToken = default) =>
        _writeLine(text, kind);
}
