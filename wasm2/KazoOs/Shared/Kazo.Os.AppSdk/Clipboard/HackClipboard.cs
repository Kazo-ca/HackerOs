using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.AppSdk.Clipboard;

/// <summary>
/// The session's clipboard (ADR 0056, section 12), shared by every app of the session: a file list
/// cut in one File Explorer window is pasted by another. Modelled on
/// <c>System.Windows.Forms.Clipboard</c> (<c>SetText</c>, <c>SetFileDropList</c>…), with GNOME's
/// <c>x-special/gnome-copied-files</c> format to tell a cut from a copy.
/// </summary>
/// <remarks>
/// Reading needs <c>clipboard.read</c>, writing needs <c>clipboard.write</c>; a missing one raises
/// <see cref="HackUnauthorizedAccessException"/>. Every member targets the app running the current
/// code (ambient context of ADR 0043); a Blazor component outside that scope calls
/// <c>AppContext.Clipboard</c> directly.
/// </remarks>
public static class HackClipboard
{
    /// <summary>
    /// Raised after any app of the session changed the clipboard. A component that unsubscribes in
    /// <c>Dispose</c> should use <c>AppContext.Clipboard.Changed</c> instead.
    /// </summary>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static event EventHandler? Changed
    {
        add => Gateway.Changed += value;
        remove => Gateway.Changed -= value;
    }

    /// <summary>Gets the clipboard as text (a file list reads as its paths, one per line), or <see langword="null"/> when empty.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>clipboard.read</c>.</exception>
    public static Task<string?> GetTextAsync(CancellationToken cancellationToken = default) =>
        CallAsync(gateway => gateway.GetTextAsync(cancellationToken));

    /// <summary>Gets whether the clipboard holds text (including the paths of a file list).</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>clipboard.read</c>.</exception>
    public static async Task<bool> ContainsTextAsync(CancellationToken cancellationToken = default) =>
        !string.IsNullOrEmpty(await GetTextAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Replaces the clipboard with <paramref name="text"/>.</summary>
    /// <param name="text">Text to copy.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>clipboard.write</c>.</exception>
    public static Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return CallAsync(async gateway =>
        {
            await gateway.SetTextAsync(text, cancellationToken).ConfigureAwait(false);
            return true;
        });
    }

    /// <summary>Gets whether the clipboard holds a file list to paste.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>clipboard.read</c>.</exception>
    public static async Task<bool> ContainsFileDropListAsync(CancellationToken cancellationToken = default) =>
        await GetFileDropListAsync(cancellationToken).ConfigureAwait(false) is { Paths.Count: > 0 };

    /// <summary>Gets the file list last cut or copied, or <see langword="null"/>.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>clipboard.read</c>.</exception>
    public static Task<HackClipboardFileList?> GetFileDropListAsync(CancellationToken cancellationToken = default) =>
        CallAsync(gateway => gateway.GetFileDropListAsync(cancellationToken));

    /// <summary>
    /// Puts files on the clipboard to cut or copy. Relative and <c>~</c> paths are resolved against the
    /// app's working directory; the files are not checked (a paste reports the missing ones).
    /// </summary>
    /// <param name="paths">Files and directories, at least one.</param>
    /// <param name="operation">Copy or cut.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="ArgumentException">No path, or an empty or malformed one.</exception>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>clipboard.write</c>.</exception>
    public static Task SetFileDropListAsync(
        IEnumerable<string> paths, HackClipboardFileOperation operation = HackClipboardFileOperation.Copy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string[] resolved = [.. paths.Select(path => HackPath.Resolve(path, nameof(paths)).Value)];
        if (resolved.Length == 0)
        {
            throw new ArgumentException("At least one path is required.", nameof(paths));
        }

        HackClipboardFileList list = new(operation, resolved);
        return CallAsync(async gateway =>
        {
            await gateway.SetFileDropListAsync(list, cancellationToken).ConfigureAwait(false);
            return true;
        });
    }

    /// <summary>Empties the clipboard, as a paste of cut files does.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>clipboard.write</c>.</exception>
    public static Task ClearAsync(CancellationToken cancellationToken = default) =>
        CallAsync(async gateway =>
        {
            await gateway.ClearAsync(cancellationToken).ConfigureAwait(false);
            return true;
        });

    private static IAppClipboardGateway Gateway => HackExecutionContext.Current.Clipboard;

    private static async Task<T> CallAsync<T>(Func<IAppClipboardGateway, ValueTask<T>> call)
    {
        try
        {
            return await call(Gateway).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw HackGatewayErrors.ToUnauthorized(exception);
        }
    }
}
