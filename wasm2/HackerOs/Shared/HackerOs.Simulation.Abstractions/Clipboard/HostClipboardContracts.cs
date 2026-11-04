namespace HackerOs.Simulation.Abstractions.Clipboard;

/// <summary>
/// The page-side half of the host clipboard bridge (ADR 0065): the piece that talks to the real
/// browser's clipboard, as the VirtualBox Guest Additions talk to the host's. Browser infrastructure
/// implements it with a small JavaScript module; tests use a fake.
/// </summary>
/// <remarks>
/// The channel never calls <c>navigator.clipboard.readText</c> (it would prompt for permission):
/// host content only enters through the <c>paste</c> events the user triggers.
/// </remarks>
public interface IHostClipboardChannel : IAsyncDisposable
{
    /// <summary>Installs the page listeners (<c>paste</c>, <c>copy</c>, <c>cut</c>) that report to <paramref name="sink"/>.</summary>
    /// <param name="sink">Receives host pastes and native copies.</param>
    /// <param name="cancellationToken">Cancels the installation.</param>
    ValueTask AttachAsync(IHostClipboardSink sink, CancellationToken cancellationToken = default);

    /// <summary>Tells the page which directions are allowed, so its listeners decide synchronously inside the event.</summary>
    /// <param name="guestToHost">Whether Kazo OS text may reach the host.</param>
    /// <param name="hostToGuest">Whether host content may enter Kazo OS.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask SetDirectionsAsync(bool guestToHost, bool hostToGuest, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells the page the text the Kazo OS clipboard now holds (<see langword="null"/> for none), and
    /// writes it to the host clipboard (<c>navigator.clipboard.writeText</c>) when
    /// <paramref name="writeToHost"/> is set.
    /// </summary>
    /// <param name="text">Text of the Kazo OS clipboard, or <see langword="null"/>.</param>
    /// <param name="writeToHost">Whether to copy it to the host too.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask SyncGuestTextAsync(string? text, bool writeToHost, CancellationToken cancellationToken = default);
}

/// <summary>Receives what the page saw of the host clipboard (ADR 0065).</summary>
public interface IHostClipboardSink
{
    /// <summary>
    /// A paste brought new content from the host clipboard (the page already filtered out the text
    /// Kazo OS itself last wrote there).
    /// </summary>
    /// <param name="text">The pasted <c>text/plain</c>, or <see langword="null"/>.</param>
    /// <param name="fileNames">Names of the host files carried by the paste; empty when none.</param>
    void OnHostPaste(string? text, IReadOnlyList<string> fileNames);

    /// <summary>A native copy or cut inside Kazo OS (a text field, the terminal) selected this text.</summary>
    /// <param name="text">Copied text.</param>
    void OnNativeCopy(string text);
}
