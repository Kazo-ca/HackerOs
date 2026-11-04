using Microsoft.JSInterop;

namespace Kazo.Os.AppSdk.Blazor.Interop;

/// <summary>
/// Opens the host computer's file picker and reads the chosen file (ADR 0099, section 1): the
/// opening counterpart of <see cref="BrowserFileDownloader"/>, through the collocated
/// <c>pickFile.js</c> module. A browser page can only read a file the user picked in its dialog.
/// </summary>
/// <remarks>
/// The dialog opens only shortly after a user gesture (a click, or Enter in a terminal); call
/// <see cref="PickAsync"/> straight from the handler of that gesture.
/// </remarks>
public sealed class BrowserFilePicker : IAsyncDisposable
{
    private readonly Lazy<Task<IJSObjectReference>> _moduleTask;

    /// <summary>Creates the picker.</summary>
    /// <param name="jsRuntime">JavaScript runtime of the host page.</param>
    public BrowserFilePicker(IJSRuntime jsRuntime)
    {
        ArgumentNullException.ThrowIfNull(jsRuntime);
        _moduleTask = new(() => jsRuntime.InvokeAsync<IJSObjectReference>(
            "import", "./_content/Kazo.Os.AppSdk.Blazor/pickFile.js").AsTask());
    }

    /// <summary>Opens the picker and reads the chosen file.</summary>
    /// <param name="accept">File types offered, as the <c>accept</c> attribute (<c>.kosbak,.json</c>); <see langword="null"/> for any.</param>
    /// <param name="cancellationToken">Stops waiting for the user.</param>
    /// <returns>The file, or <see langword="null"/> when the user closed the picker.</returns>
    /// <exception cref="InvalidOperationException">No recent user gesture: the browser would not open the picker.</exception>
    public async ValueTask<BrowserPickedFile?> PickAsync(string? accept = null, CancellationToken cancellationToken = default)
    {
        IJSObjectReference module = await _moduleTask.Value.ConfigureAwait(false);
        IJSObjectReference? file;
        try
        {
            file = await module.InvokeAsync<IJSObjectReference?>("pickFile", cancellationToken, accept).ConfigureAwait(false);
        }
        catch (JSException exception) when (exception.Message.Contains("user activation required", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "the browser opens its file dialog only right after a click or a key press", exception);
        }

        if (file is null)
        {
            return null;
        }

        await using (file.ConfigureAwait(false))
        {
            string name = await module.InvokeAsync<string>("fileName", cancellationToken, file).ConfigureAwait(false);
            byte[] content = await module.InvokeAsync<byte[]>("readFile", cancellationToken, file).ConfigureAwait(false);
            return new BrowserPickedFile(name, content);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_moduleTask.IsValueCreated)
        {
            IJSObjectReference module = await _moduleTask.Value.ConfigureAwait(false);
            await module.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>A file the user picked on the host.</summary>
/// <param name="Name">File name, without its host path.</param>
/// <param name="Content">Whole content.</param>
public sealed record BrowserPickedFile(string Name, byte[] Content);
