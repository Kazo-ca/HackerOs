using Kazo.Os.AppSdk.Clipboard;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Clipboard;

/// <summary>
/// The OS clipboard (ADR 0056, section 12; ADR 0065): one selection per session, owned by the platform
/// and shared by every app of that session, like the X11 <c>CLIPBOARD</c> selection a desktop's
/// clipboard manager keeps alive. Content is held as one <see cref="SystemClipboardEntry"/> of
/// MIME-typed targets, as X11 offers them: <c>text/plain</c>, <c>x-special/gnome-copied-files</c> for a
/// file list, or any other format. Every change gets a new sequence number, like Windows'
/// <c>GetClipboardSequenceNumber</c>. It lives in memory only: like a real clipboard, it does not
/// survive a reboot, and nothing of it is ever persisted or encrypted.
/// </summary>
/// <remarks>
/// Apps never see this class; each instance gets an <see cref="Execution.AppClipboardGateway"/> that
/// checks <c>clipboard.read</c>/<c>clipboard.write</c> and only reaches its own session. The only other
/// reader is the <see cref="HostClipboardBridge"/>, which mirrors <c>text/plain</c> to the real
/// browser's clipboard as VirtualBox Guest Additions do (ADR 0065).
/// </remarks>
public sealed class SystemClipboard : IDisposable
{
    /// <summary>The <c>text/plain</c> target.</summary>
    public const string TextMediaType = "text/plain";

    private readonly Lock _gate = new();
    private readonly Dictionary<SessionId, SystemClipboardEntry> _selections = [];
    private readonly Dictionary<SessionId, long> _sequences = [];
    private readonly Dictionary<SessionId, KernelMemoryCharge> _charges = [];
    private readonly IKernelMemoryLedger? _ledger;
    private readonly IDisposable? _logoutSubscription;

    /// <summary>Creates an empty clipboard.</summary>
    /// <param name="eventBus">
    /// When supplied, a session's clipboard is emptied at its logout, as the X server drops the
    /// selections of a closed desktop session.
    /// </param>
    /// <param name="ledger">
    /// When supplied and tracking (memory measured, ADR 0068), the content an app puts on the
    /// clipboard is charged to its process until it is replaced or cleared, as an X11 selection lives
    /// in its owner's memory.
    /// </param>
    public SystemClipboard(IEventBus? eventBus = null, IKernelMemoryLedger? ledger = null)
    {
        _ledger = ledger;
        _logoutSubscription = eventBus?.Subscribe<SessionLoggedOutEvent>(logout => Clear(logout.SessionId));
    }

    /// <summary>Raised after a session's clipboard changed, with that session's ID. Handlers run on the writer's call stack.</summary>
    public event EventHandler<SessionId>? Changed;

    /// <summary>Gets one target of a session's clipboard, or <see langword="null"/> when it is not offered.</summary>
    /// <param name="session">Owning session.</param>
    /// <param name="mediaType">Target MIME type.</param>
    public string? Get(SessionId session, string mediaType)
    {
        lock (_gate)
        {
            return _selections.TryGetValue(session, out SystemClipboardEntry? entry)
                && entry.Targets.TryGetValue(mediaType, out string? content)
                    ? content
                    : null;
        }
    }

    /// <summary>Gets a session's whole clipboard entry, or <see langword="null"/> when it is empty.</summary>
    /// <param name="session">Owning session.</param>
    public SystemClipboardEntry? GetEntry(SessionId session)
    {
        lock (_gate)
        {
            return _selections.GetValueOrDefault(session);
        }
    }

    /// <summary>
    /// Gets a session's clipboard sequence number: 0 before the first change, then incremented by every
    /// change (including clearing), so a reader can tell whether the clipboard changed since it last looked.
    /// </summary>
    /// <param name="session">Owning session.</param>
    public long GetSequenceNumber(SessionId session)
    {
        lock (_gate)
        {
            return _sequences.GetValueOrDefault(session);
        }
    }

    /// <summary>Replaces a session's clipboard with text.</summary>
    /// <param name="session">Owning session.</param>
    /// <param name="text">Copied text.</param>
    /// <param name="origin">Who put the text there; <see cref="SystemClipboardOrigin.Guest"/> for an app.</param>
    /// <param name="owner">The process that copied, charged with the content's memory (ADR 0068); <see langword="null"/> for the OS or the host.</param>
    public void SetText(SessionId session, string text, SystemClipboardOrigin origin = SystemClipboardOrigin.Guest, ProcessId? owner = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        SetTargets(session, new Dictionary<string, string>(StringComparer.Ordinal) { [TextMediaType] = text }, origin, owner);
    }

    /// <summary>
    /// Replaces a session's clipboard with a file list, offered both as
    /// <c>x-special/gnome-copied-files</c> and as <c>text/plain</c> (the paths, one per line).
    /// </summary>
    /// <param name="session">Owning session.</param>
    /// <param name="files">Files cut or copied.</param>
    /// <param name="owner">The process that copied, charged with the content's memory (ADR 0068).</param>
    public void SetFileDropList(SessionId session, HackClipboardFileList files, ProcessId? owner = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        SetTargets(session, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HackClipboardFileList.GnomeCopiedFilesMediaType] = files.ToGnomeCopiedFiles(),
            [TextMediaType] = files.ToText()
        }, SystemClipboardOrigin.Guest, owner);
    }

    /// <summary>
    /// Replaces a session's clipboard with any set of formats (the extensible form of
    /// <see cref="SetText"/> and <see cref="SetFileDropList"/>). An empty set clears the clipboard.
    /// </summary>
    /// <param name="session">Owning session.</param>
    /// <param name="targets">Content per MIME type; copied, so later changes to the dictionary are not seen.</param>
    /// <param name="origin">Who put the content there.</param>
    /// <param name="owner">The process that copied, charged with the content's memory (ADR 0068).</param>
    /// <exception cref="ArgumentException">A media type is empty or a content is <see langword="null"/>.</exception>
    public void SetTargets(SessionId session, IReadOnlyDictionary<string, string> targets, SystemClipboardOrigin origin, ProcessId? owner = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        Dictionary<string, string> copy = new(StringComparer.Ordinal);
        foreach ((string mediaType, string content) in targets)
        {
            if (string.IsNullOrWhiteSpace(mediaType) || content is null)
            {
                throw new ArgumentException("Every clipboard target needs a media type and a content.", nameof(targets));
            }

            copy[mediaType] = content;
        }

        if (copy.Count == 0)
        {
            Clear(session);
            return;
        }

        KernelMemoryCharge? charge = owner is { } pid && _ledger is { IsTracking: true } ledger
            ? ledger.Charge(pid, KernelMemoryKind.Clipboard, ContentBytes(copy))
            : null;
        KernelMemoryCharge? previous;
        lock (_gate)
        {
            long sequence = _sequences.GetValueOrDefault(session) + 1;
            _sequences[session] = sequence;
            _selections[session] = new SystemClipboardEntry(copy, sequence, origin);
            previous = SwapCharge(session, charge);
        }

        previous?.Dispose();

        Changed?.Invoke(this, session);
    }

    /// <summary>Empties a session's clipboard, for instance after cut files were pasted or at logout.</summary>
    /// <param name="session">Owning session.</param>
    public void Clear(SessionId session)
    {
        bool removed;
        KernelMemoryCharge? previous;
        lock (_gate)
        {
            removed = _selections.Remove(session);
            if (removed)
            {
                _sequences[session] = _sequences.GetValueOrDefault(session) + 1;
            }

            previous = SwapCharge(session, null);
        }

        previous?.Dispose();

        if (removed)
        {
            Changed?.Invoke(this, session);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _logoutSubscription?.Dispose();

    /// <summary>Bytes the content occupies: UTF-16 text, two bytes per character, every target included.</summary>
    private static long ContentBytes(Dictionary<string, string> targets)
    {
        long characters = 0;
        foreach ((string mediaType, string content) in targets)
        {
            characters += mediaType.Length + content.Length;
        }

        return characters * sizeof(char);
    }

    /// <summary>Replaces a session's charge under the lock; the caller disposes the returned previous one outside it.</summary>
    private KernelMemoryCharge? SwapCharge(SessionId session, KernelMemoryCharge? charge)
    {
        _charges.Remove(session, out KernelMemoryCharge? previous);
        if (charge is not null)
        {
            _charges[session] = charge;
        }

        return previous;
    }
}

/// <summary>One clipboard content: its formats, the sequence number of the change that set it and its origin.</summary>
/// <param name="Targets">Content per MIME type (<c>text/plain</c>, <c>x-special/gnome-copied-files</c>…).</param>
/// <param name="SequenceNumber">Sequence number of the change (see <see cref="SystemClipboard.GetSequenceNumber"/>).</param>
/// <param name="Origin">Who put it there.</param>
public sealed record SystemClipboardEntry(IReadOnlyDictionary<string, string> Targets, long SequenceNumber, SystemClipboardOrigin Origin)
{
    /// <summary>Gets the <c>text/plain</c> target, or <see langword="null"/> when the entry has none.</summary>
    public string? Text => Targets.GetValueOrDefault(SystemClipboard.TextMediaType);
}

/// <summary>Who put the current content on the OS clipboard (ADR 0065).</summary>
public enum SystemClipboardOrigin
{
    /// <summary>An app, through <c>HackClipboard</c>: the bridge mirrors its text to the host.</summary>
    Guest,

    /// <summary>
    /// A native copy or cut inside Kazo OS (a text field, the terminal): the browser itself already
    /// handled the host side, so the bridge does not write it again.
    /// </summary>
    GuestNative,

    /// <summary>The real machine's clipboard, received through a paste: never echoed back to the host.</summary>
    Host
}
