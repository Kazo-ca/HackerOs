using Kazo.Os.Simulation.Abstractions.Metrics;

namespace Kazo.Os.Platform.Core.Metrics;

/// <summary>
/// In-memory <see cref="IBlockDeviceStatistics"/> of the OS disk <c>hda</c> (major 8, minor 0), the
/// IndexedDB database behind <c>/</c> (ADR 0057 section 4).
/// </summary>
/// <remarks>
/// Linux accounts <c>io_ticks</c> (time with at least one request in flight) and
/// <c>time_in_queue</c> (time weighted by queue depth) from a timestamp updated at every queue
/// change. This class does the same with a monotonic clock: each <see cref="Begin"/> and
/// <see cref="Complete"/> first folds the time since the previous change into both counters, using
/// the queue depth that was in force during it.
/// </remarks>
public sealed class BlockDeviceStatistics : IBlockDeviceStatistics
{
    /// <summary>Size of one sector in <c>/proc/diskstats</c>, always 512 bytes whatever the device.</summary>
    public const int SectorSize = 512;

    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private long _readsCompleted;
    private long _sectorsRead;
    private double _millisecondsReading;
    private long _writesCompleted;
    private long _sectorsWritten;
    private double _millisecondsWriting;
    private long _inFlight;
    private double _millisecondsDoingIo;
    private double _weightedMilliseconds;
    private long _lastChange;

    /// <summary>Initializes the statistics of <c>hda</c>.</summary>
    /// <param name="timeProvider">Monotonic time source for the busy-time counters; real time by default.</param>
    public BlockDeviceStatistics(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastChange = _timeProvider.GetTimestamp();
    }

    /// <inheritdoc />
    public string DeviceName => "hda";

    /// <inheritdoc />
    public int Major => 8;

    /// <inheritdoc />
    public int Minor => 0;

    /// <inheritdoc />
    public void Begin()
    {
        lock (_gate)
        {
            AccountQueueTime();
            _inFlight++;
        }
    }

    /// <inheritdoc />
    public void Complete(BlockIoDirection direction, long bytes, TimeSpan elapsed, bool succeeded)
    {
        long sectors = (Math.Max(0, bytes) + SectorSize - 1) / SectorSize;
        double milliseconds = Math.Max(0, elapsed.TotalMilliseconds);
        lock (_gate)
        {
            AccountQueueTime();
            _inFlight = Math.Max(0, _inFlight - 1);
            if (!succeeded)
            {
                return;
            }

            if (direction == BlockIoDirection.Read)
            {
                _readsCompleted++;
                _sectorsRead += sectors;
                _millisecondsReading += milliseconds;
            }
            else
            {
                _writesCompleted++;
                _sectorsWritten += sectors;
                _millisecondsWriting += milliseconds;
            }
        }
    }

    /// <inheritdoc />
    public BlockDeviceCounters GetCounters()
    {
        lock (_gate)
        {
            AccountQueueTime();
            return new BlockDeviceCounters(
                _readsCompleted,
                ReadsMerged: 0,
                _sectorsRead,
                (long)_millisecondsReading,
                _writesCompleted,
                WritesMerged: 0,
                _sectorsWritten,
                (long)_millisecondsWriting,
                _inFlight,
                (long)_millisecondsDoingIo,
                (long)_weightedMilliseconds);
        }
    }

    /// <summary>Folds the time since the previous queue change into the busy counters. Caller holds the lock.</summary>
    private void AccountQueueTime()
    {
        long now = _timeProvider.GetTimestamp();
        double elapsed = _timeProvider.GetElapsedTime(_lastChange, now).TotalMilliseconds;
        _lastChange = now;
        if (_inFlight > 0 && elapsed > 0)
        {
            _millisecondsDoingIo += elapsed;
            _weightedMilliseconds += elapsed * _inFlight;
        }
    }
}
