using System.Globalization;

namespace Kazo.Os.AppSdk.Diagnostics;

/// <summary>
/// The process running the current code, as seen from inside (ADR 0069, section 1), returned by
/// <see cref="HackProcess.Current"/>: its PID, its stop token and the <see cref="StopRequested"/>
/// notice, modeled on <c>IHostApplicationLifetime.ApplicationStopping</c>.
/// </summary>
/// <example>
/// <code>
/// HackProcess.Current.StopRequested += (_, e) =&gt; queue.CompleteAdding();
/// while (queue.TryTake(out Job job))
/// {
///     await RunAsync(job);
///     await HackProcess.CheckpointAsync(HackProcess.Current.StopToken);
/// }
/// </code>
/// </example>
/// <remarks>
/// Unlike <see cref="HackProcess"/>, it reads nothing from <c>/proc</c>: it is a thin view of the
/// app's own <see cref="IAppExecutionContext"/>, bound when <see cref="HackProcess.Current"/> is read.
/// </remarks>
public sealed class HackCurrentProcess
{
    private readonly IAppExecutionContext _context;

    internal HackCurrentProcess(IAppExecutionContext context) => _context = context;

    /// <summary>Gets the process identifier (PID).</summary>
    public long Id => _context.ProcessId.Value;

    /// <summary>
    /// Gets the token the OS cancels when it stops the process (close, <c>kill</c>, logout,
    /// shutdown), right after <see cref="StopRequested"/>.
    /// </summary>
    public CancellationToken StopToken => _context.CancellationToken;

    /// <summary>Gets whether the OS has asked the process to stop.</summary>
    public bool IsStopRequested => _context.StopRequest is not null || _context.CancellationToken.IsCancellationRequested;

    /// <summary>
    /// Gets the stop request already received, or <see langword="null"/>: a handler attached after
    /// the request is not called, so code that subscribes late reads this first.
    /// </summary>
    public HackStopRequestedEventArgs? StopRequest => _context.StopRequest;

    /// <summary>
    /// Raised once when the OS asks the process to stop, before <see cref="StopToken"/> is cancelled
    /// (see <see cref="HackStopRequestedEventArgs"/>).
    /// </summary>
    public event EventHandler<HackStopRequestedEventArgs>? StopRequested
    {
        add => _context.StopRequested += value;
        remove => _context.StopRequested -= value;
    }

    /// <inheritdoc />
    public override string ToString() => $"{_context.Manifest.Id} ({Id.ToString(CultureInfo.InvariantCulture)})";
}
