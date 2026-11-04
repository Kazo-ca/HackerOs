using System.Diagnostics;
using Kazo.Os.AppSdk.Diagnostics;

namespace Kazo.Os.AppSdk.Threading;

/// <summary>
/// What a background task receives with its input (ADR 0128, section 1): its cancellation, its
/// progress reports and its checkpoints. The rest of the SDK (<c>HackFile</c>, <c>HackProcess</c>,
/// <c>HackEnvironment</c>…) works inside the task as in any process: in a Web Worker, through the
/// remote gateways (ADR 0124).
/// </summary>
public sealed class HackBackgroundContext
{
    /// <summary>Reports are delivered at most this often (10 a second, ADR 0128 section 4).</summary>
    internal static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);

    private readonly Action<HackBackgroundProgress>? _sink;
    private readonly Lock _gate = new();
    private long _lastSent = long.MinValue;
    private HackBackgroundProgress? _pending;
    private bool _flushScheduled;
    private bool _closed;

    /// <summary>Initializes the context of one task's process.</summary>
    /// <param name="processId">The task's own process.</param>
    /// <param name="cancellationToken">Cancelled when the caller cancels or the process is asked to stop.</param>
    /// <param name="sink">Carries a throttled report to the caller, or <see langword="null"/> when nobody listens.</param>
    internal HackBackgroundContext(long processId, CancellationToken cancellationToken, Action<HackBackgroundProgress>? sink)
    {
        ProcessId = processId;
        CancellationToken = cancellationToken;
        _sink = sink;
    }

    /// <summary>Gets the task's process identifier (a child of the app that started it).</summary>
    public long ProcessId { get; }

    /// <summary>
    /// Gets the token cancelled when the caller cancels the task, when the app closes, or when the
    /// task is asked to stop (<c>kill</c> without <c>-9</c>).
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Reports progress to the caller. Reports closer than 100 ms are merged: the caller receives the
    /// latest one, and the last report before the end is always delivered.
    /// </summary>
    /// <param name="fraction">Done so far, from <c>0</c> to <c>1</c> (clamped).</param>
    /// <param name="message">What the task is doing, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fraction"/> is not a number.</exception>
    public void Report(double fraction, string? message = null)
    {
        if (double.IsNaN(fraction))
        {
            throw new ArgumentOutOfRangeException(nameof(fraction), "The fraction must be a number between 0 and 1.");
        }

        if (_sink is null)
        {
            return;
        }

        HackBackgroundProgress progress = new(Math.Clamp(fraction, 0d, 1d), message);
        TimeSpan wait;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            TimeSpan since = _lastSent == long.MinValue ? TimeSpan.MaxValue : Stopwatch.GetElapsedTime(_lastSent, now);
            if (since >= ReportInterval)
            {
                _lastSent = now;
                _pending = null;
                wait = TimeSpan.Zero;
            }
            else
            {
                // Too soon: keep the latest, and send it once the interval has passed.
                _pending = progress;
                if (_flushScheduled)
                {
                    return;
                }

                _flushScheduled = true;
                wait = ReportInterval - since;
            }
        }

        if (wait == TimeSpan.Zero)
        {
            _sink(progress);
        }
        else
        {
            _ = FlushLaterAsync(wait);
        }
    }

    /// <summary>
    /// A voluntary checkpoint for long work, like <see cref="HackProcess.CheckpointAsync"/> (ADR 0069):
    /// throws when the task is cancelled, and gives the thread back when the current time slice
    /// exceeds 8 ms. On the main thread, it keeps the desktop alive; in a worker, it is the moment the
    /// worker can receive "stop".
    /// </summary>
    /// <returns>A completed task, or one that completes after the thread was given back.</returns>
    /// <exception cref="OperationCanceledException">The task is cancelled or asked to stop.</exception>
    public ValueTask CheckpointAsync() => HackProcess.CheckpointAsync(CancellationToken);

    /// <summary>Sends the pending report, if any, and refuses later ones: the task has ended.</summary>
    internal void Close()
    {
        HackBackgroundProgress? pending;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            pending = _pending;
            _pending = null;
        }

        if (pending is { } last)
        {
            _sink?.Invoke(last);
        }
    }

    private async Task FlushLaterAsync(TimeSpan wait)
    {
        try
        {
            await Task.Delay(wait).ConfigureAwait(true);
        }
        finally
        {
            HackBackgroundProgress? pending;
            lock (_gate)
            {
                _flushScheduled = false;
                pending = _closed ? null : _pending;
                _pending = null;
                if (pending is not null)
                {
                    _lastSent = Stopwatch.GetTimestamp();
                }
            }

            if (pending is { } progress)
            {
                _sink!(progress);
            }
        }
    }
}
