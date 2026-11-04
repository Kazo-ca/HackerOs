using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Clipboard;
using Kazo.Os.Platform.Core.Clipboard;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Execution;

/// <summary>
/// Trusted <see cref="IAppClipboardGateway"/> (ADR 0056, section 12): one app instance's access to
/// its own session's <see cref="SystemClipboard"/>, checking <c>clipboard.read</c> on every read and
/// <c>clipboard.write</c> on every write.
/// </summary>
internal sealed class AppClipboardGateway : IAppClipboardGateway
{
    private readonly SystemClipboard _clipboard;
    private readonly ICapabilityChecker _capabilities;
    private readonly SessionId _session;
    private readonly ProcessId? _owner;
    private readonly AppAmbientContextBinding _ambient = new();
    private readonly Lock _gate = new();
    private EventHandler? _changed;

    /// <summary>Creates the gateway for one app instance of <paramref name="session"/>.</summary>
    /// <param name="clipboard">The OS clipboard.</param>
    /// <param name="capabilities">Capability checker bound to the app and its user.</param>
    /// <param name="session">The app's session, the only clipboard it reaches.</param>
    /// <param name="owner">The app's process, charged with what it copies when memory is measured (ADR 0068).</param>
    public AppClipboardGateway(SystemClipboard clipboard, ICapabilityChecker capabilities, SessionId session, ProcessId? owner = null)
    {
        _owner = owner;
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _session = session;
    }

    /// <inheritdoc />
    public event EventHandler? Changed
    {
        add
        {
            lock (_gate)
            {
                bool first = _changed is null;
                _changed += value;
                if (first && _changed is not null)
                {
                    _clipboard.Changed += OnClipboardChanged;
                }
            }
        }
        remove
        {
            lock (_gate)
            {
                bool had = _changed is not null;
                _changed -= value;
                if (had && _changed is null)
                {
                    _clipboard.Changed -= OnClipboardChanged;
                }
            }
        }
    }

    /// <summary>Binds the app context <see cref="Changed"/> handlers run under; called once by the factory.</summary>
    internal void BindAmbientContext(Kazo.Os.AppSdk.IAppExecutionContext context) => _ambient.Bind(context);

    /// <inheritdoc />
    public ValueTask<string?> GetTextAsync(CancellationToken cancellationToken = default)
    {
        // ADR 0067: time spent in the gateway is the caller's stime (kernel sub-frame).
        using Kazo.Os.AppSdk.HackExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.HackExecutionContext.EnterKernel();
        cancellationToken.ThrowIfCancellationRequested();
        _capabilities.Require(AppCapabilities.ClipboardRead);
        return ValueTask.FromResult(_clipboard.Get(_session, SystemClipboard.TextMediaType));
    }

    /// <inheritdoc />
    public ValueTask SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        // ADR 0067: time spent in the gateway is the caller's stime (kernel sub-frame).
        using Kazo.Os.AppSdk.HackExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.HackExecutionContext.EnterKernel();
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        _capabilities.Require(AppCapabilities.ClipboardWrite);
        _clipboard.SetText(_session, text, owner: _owner);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<HackClipboardFileList?> GetFileDropListAsync(CancellationToken cancellationToken = default)
    {
        // ADR 0067: time spent in the gateway is the caller's stime (kernel sub-frame).
        using Kazo.Os.AppSdk.HackExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.HackExecutionContext.EnterKernel();
        cancellationToken.ThrowIfCancellationRequested();
        _capabilities.Require(AppCapabilities.ClipboardRead);
        return ValueTask.FromResult(HackClipboardFileList.TryParseGnomeCopiedFiles(
            _clipboard.Get(_session, HackClipboardFileList.GnomeCopiedFilesMediaType)));
    }

    /// <inheritdoc />
    public ValueTask SetFileDropListAsync(HackClipboardFileList files, CancellationToken cancellationToken = default)
    {
        // ADR 0067: time spent in the gateway is the caller's stime (kernel sub-frame).
        using Kazo.Os.AppSdk.HackExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.HackExecutionContext.EnterKernel();
        ArgumentNullException.ThrowIfNull(files);
        cancellationToken.ThrowIfCancellationRequested();
        _capabilities.Require(AppCapabilities.ClipboardWrite);
        foreach (string path in files.Paths)
        {
            // Only canonical absolute paths reach the shared clipboard: another app resolves them as is.
            VirtualPath.Parse(path);
        }

        _clipboard.SetFileDropList(_session, files, _owner);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        // ADR 0067: time spent in the gateway is the caller's stime (kernel sub-frame).
        using Kazo.Os.AppSdk.HackExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.HackExecutionContext.EnterKernel();
        cancellationToken.ThrowIfCancellationRequested();
        _capabilities.Require(AppCapabilities.ClipboardWrite);
        _clipboard.Clear(_session);
        return ValueTask.CompletedTask;
    }

    private void OnClipboardChanged(object? sender, SessionId session)
    {
        if (session != _session)
        {
            return;
        }

        EventHandler? handlers;
        lock (_gate)
        {
            handlers = _changed;
        }

        if (handlers is not null)
        {
            _ambient.Wrap(() => handlers(this, EventArgs.Empty))();
        }
    }
}
