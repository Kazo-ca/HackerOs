using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.Clipboard;
using HackerOs.Simulation.Abstractions.Sessions;
using HackerOs.Simulation.Abstractions.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HackerOs.Platform.Core.Clipboard;

/// <summary>
/// Shares text between the <see cref="SystemClipboard"/> of the current session and the real machine's
/// clipboard (ADR 0065), like the VirtualBox Guest Additions' "Shared Clipboard":
/// <list type="bullet">
/// <item><b>guest to host</b>: when an app puts content with <c>text/plain</c> on the clipboard (a file
/// list included, as its paths), the text is written to the host with
/// <c>navigator.clipboard.writeText</c>, within the user gesture (Ctrl+C, a click) that caused it;</item>
/// <item><b>host to guest</b>: a <c>paste</c> event whose text differs from what KazoOS last wrote
/// becomes the current entry (origin <see cref="SystemClipboardOrigin.Host"/>); host files pasted
/// are recorded by name under <see cref="HostFilesMediaType"/> and imported by the app that receives
/// the paste (the File Explorer), like a drop from the host;</item>
/// <item>native copies inside KazoOS (a text field, the terminal) also become the current entry.</item>
/// </list>
/// The direction is the <c>sharing</c> key of <c>/etc/hackeros/clipboard.config</c>
/// (<see cref="ClipboardSettingsDocuments"/>), re-read whenever the file or the session changes.
/// </summary>
/// <remarks>
/// Internal formats (a file list's GNOME target, any custom format) never leave the OS memory: only
/// <c>text/plain</c> goes to the host. The bridge is optional: a channel failure (no clipboard API,
/// permission refused) is logged and never breaks the OS clipboard itself.
/// </remarks>
public sealed class HostClipboardBridge : IHostClipboardSink, IAsyncDisposable
{
    /// <summary>
    /// Target recording the names of host files a paste carried (one per line). The files themselves
    /// stay in the page and are imported by the app that received the paste.
    /// </summary>
    public const string HostFilesMediaType = "application/x-hackeros-host-files";

    private readonly SystemClipboard _clipboard;
    private readonly ISessionService _sessions;
    private readonly IOsSettingsService _settings;
    private readonly IHostClipboardChannel _channel;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private Task _queue = Task.CompletedTask;
    private SessionId? _syncedSession;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates the bridge; nothing is shared before <see cref="StartAsync"/>.</summary>
    /// <param name="clipboard">The OS clipboard.</param>
    /// <param name="sessions">Gives the current session, the only one shared with the host.</param>
    /// <param name="settings">Reads <c>clipboard.config</c>.</param>
    /// <param name="channel">Page-side half talking to the browser.</param>
    /// <param name="logger">Receives channel failures.</param>
    public HostClipboardBridge(
        SystemClipboard clipboard,
        ISessionService sessions,
        IOsSettingsService settings,
        IHostClipboardChannel channel,
        ILogger<HostClipboardBridge>? logger = null)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Gets the effective sharing mode (<see cref="ClipboardSharingMode.Bidirectional"/> until the settings are read).</summary>
    public ClipboardSharingMode Mode { get; private set; } = ClipboardSharingMode.Bidirectional;

    /// <summary>
    /// Reads the sharing mode, installs the page listeners and starts mirroring. Idempotent; a failure
    /// is logged and leaves the OS clipboard working without host sharing.
    /// </summary>
    /// <param name="cancellationToken">Cancels the start.</param>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
        }

        _clipboard.Changed += OnClipboardChanged;
        _settings.DocumentChanged += OnSettingsChanged;
        try
        {
            await _channel.AttachAsync(this, cancellationToken).ConfigureAwait(false);
            await RefreshModeAsync(cancellationToken).ConfigureAwait(false);
            await _settings.WatchAsync(ClipboardSettingsDocuments.Document, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "clipboard: host clipboard sharing unavailable");
        }
    }

    /// <inheritdoc />
    public void OnHostPaste(string? text, IReadOnlyList<string> fileNames)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        if (!Mode.AllowsHostToGuest() || CurrentSession() is not { } session)
        {
            return;
        }

        Dictionary<string, string> targets = new(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(text))
        {
            targets[SystemClipboard.TextMediaType] = text;
        }

        if (fileNames.Count > 0)
        {
            targets[HostFilesMediaType] = string.Join('\n', fileNames);
        }

        if (targets.Count == 0)
        {
            return;
        }

        SystemClipboardEntry? current = _clipboard.GetEntry(session);
        if (current is not null && SameTargets(current.Targets, targets))
        {
            // Already the current entry (a second paste of the same host content): no new change.
            return;
        }

        _clipboard.SetTargets(session, targets, SystemClipboardOrigin.Host);
    }

    /// <inheritdoc />
    public void OnNativeCopy(string text)
    {
        if (string.IsNullOrEmpty(text) || CurrentSession() is not { } session)
        {
            return;
        }

        if (_clipboard.GetEntry(session) is { Origin: not SystemClipboardOrigin.Guest } current
            && current.Targets.Count == 1
            && current.Text == text)
        {
            return;
        }

        _clipboard.SetText(session, text, SystemClipboardOrigin.GuestNative);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _clipboard.Changed -= OnClipboardChanged;
        _settings.DocumentChanged -= OnSettingsChanged;
        try
        {
            await _queue.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Already logged by the queued call.
        }
    }

    /// <summary>Waits until every queued page call ran (tests and orderly shutdown).</summary>
    internal Task FlushAsync()
    {
        lock (_gate)
        {
            return _queue;
        }
    }

    private SessionId? CurrentSession() => _sessions.CurrentPrincipal?.SessionId;

    private void OnClipboardChanged(object? sender, SessionId session)
    {
        SessionId? current = CurrentSession();
        if (session != current && session != _syncedSession)
        {
            return;
        }

        SystemClipboardEntry? entry = session == current ? _clipboard.GetEntry(session) : null;
        if (entry is { Origin: not SystemClipboardOrigin.Guest })
        {
            // A host paste or a native copy: the page already knows this text, and the host has it.
            _syncedSession = session;
            return;
        }

        // An app wrote (or the clipboard was emptied, e.g. at logout, so the page forgets the text).
        string? text = entry?.Text;
        bool writeToHost = text is not null && Mode.AllowsGuestToHost();
        _syncedSession = entry is null ? null : session;
        Enqueue(cancellationToken => _channel.SyncGuestTextAsync(text, writeToHost, cancellationToken));
    }

    private void OnSettingsChanged(object? sender, OsSettingsChangedEventArgs args)
    {
        if (args.Concerns(ClipboardSettingsDocuments.Document.DocumentId))
        {
            Enqueue(RefreshModeAsync);
        }
    }

    private async ValueTask RefreshModeAsync(CancellationToken cancellationToken)
    {
        LayeredConfigSnapshot snapshot = await _settings
            .ReadAsync(ClipboardSettingsDocuments.Document, cancellationToken)
            .ConfigureAwait(false);
        Mode = ClipboardSettingsDocuments.ToMode(snapshot);
        await _channel.SetDirectionsAsync(Mode.AllowsGuestToHost(), Mode.AllowsHostToGuest(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs page calls one after the other, in the order the changes happened.</summary>
    private void Enqueue(Func<CancellationToken, ValueTask> call)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _queue = RunAfterAsync(_queue, call);
        }
    }

    private async Task RunAfterAsync(Task previous, Func<CancellationToken, ValueTask> call)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The previous call logged its own failure; the chain goes on.
        }

        try
        {
            await call(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The host refused (no focus, no permission, no clipboard API): KazoOS keeps its clipboard.
            _logger.LogDebug(exception, "clipboard: host clipboard call failed");
        }
    }

    private static bool SameTargets(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out string? value) && value == pair.Value);
}
