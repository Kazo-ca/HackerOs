namespace Kazo.Os.AppSdk.Clipboard;

/// <summary>
/// The session's clipboard as one app instance sees it (ADR 0056, section 12), behind
/// <see cref="HackClipboard"/>. There is one clipboard per session, shared by every app of that
/// session, like the X11 <c>CLIPBOARD</c> selection: what one window copies, another pastes.
/// </summary>
/// <remarks>
/// Reading requires <c>clipboard.read</c>, writing requires <c>clipboard.write</c> (both normal
/// capabilities). A missing one raises
/// <see cref="Kazo.Os.Simulation.Abstractions.Gateways.AppGatewayAccessDeniedException"/>; the
/// <see cref="HackClipboard"/> facade turns it into <c>HackUnauthorizedAccessException</c>.
/// </remarks>
public interface IAppClipboardGateway
{
    /// <summary>
    /// Raised after any app of the session changed the clipboard. Subscribing needs no capability
    /// (only the fact that it changed is told); handlers run inside the subscribing app's ambient scope.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>
    /// Gets the clipboard as text: the text last copied, or the paths of a file list one per line, as
    /// GNOME offers <c>text/plain</c> next to <c>x-special/gnome-copied-files</c>. <see langword="null"/>
    /// when the clipboard is empty. Requires <c>clipboard.read</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask<string?> GetTextAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces the clipboard with text. Requires <c>clipboard.write</c>.</summary>
    /// <param name="text">Text to copy.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask SetTextAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the file list last cut or copied, or <see langword="null"/> when the clipboard holds no
    /// files. Requires <c>clipboard.read</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask<HackClipboardFileList?> GetFileDropListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the clipboard with a file list to cut or copy, stored in the GNOME
    /// <c>x-special/gnome-copied-files</c> format. Requires <c>clipboard.write</c>.
    /// </summary>
    /// <param name="files">Operation and absolute paths.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask SetFileDropListAsync(HackClipboardFileList files, CancellationToken cancellationToken = default);

    /// <summary>Empties the clipboard. Requires <c>clipboard.write</c>.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}
