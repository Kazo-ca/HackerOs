namespace Kazo.Os.Simulation.Abstractions.Processes;

/// <summary>
/// The measured memory of one process, in the four layers of ADR 0068 (section 1), as
/// <c>/proc/&lt;pid&gt;/status</c> shows them.
/// </summary>
/// <param name="ImageBytes">
/// <c>VmExe</c>: the app's own assembly, exact (from the host's catalog). Several apps built into one
/// assembly (the commands) each map the whole of it, as every BusyBox applet maps the BusyBox binary.
/// </param>
/// <param name="FileBytes">
/// <c>RssFile</c>: <paramref name="ImageBytes"/> plus the shared assemblies the app uses (SDK,
/// libraries). Like a shared library on Linux, a shared assembly appears in the <c>RssFile</c> of
/// every process using it, but <c>/proc/meminfo</c> counts it once.
/// </param>
/// <param name="AnonymousBytes">
/// <c>RssAnon</c>: the process's share of the real managed heap, estimated after each garbage
/// collection from its recent allocations.
/// </param>
/// <param name="KernelBytes">
/// <c>VmData</c> (kernel part): bytes the OS holds for the process, exact: stream buffers,
/// clipboard content, pipe buffers, sockets in flight, and a fixed cost per open window.
/// </param>
/// <param name="AllocatedBytes">
/// <c>KazoAllocBytes</c>: every managed byte the process allocated since it started, exact, read at
/// each context switch.
/// </param>
public readonly record struct ProcessMemoryUsage(
    long ImageBytes,
    long FileBytes,
    long AnonymousBytes,
    long KernelBytes,
    long AllocatedBytes)
{
    /// <summary>Gets <c>VmRSS</c> = <c>RssAnon</c> + <c>RssFile</c> + kernel part (ADR 0068, section 1).</summary>
    public long ResidentBytes => AnonymousBytes + FileBytes + KernelBytes;

    /// <summary>Gets the private memory (<c>RssAnon</c>), read by <c>KazoProcess.PrivateMemorySize64</c>.</summary>
    public long PrivateBytes => AnonymousBytes;
}

/// <summary>The measured memory of the machine, behind <c>/proc/meminfo</c> (ADR 0068, section 2).</summary>
/// <param name="AnonymousBytes"><c>AnonPages</c>: the sum of every measured process's <c>RssAnon</c>.</param>
/// <param name="MappedBytes"><c>Mapped</c>: every assembly mapped by a live process, each counted once.</param>
/// <param name="KernelBytes">
/// <c>Slab</c>: what the OS holds, both for processes (their kernel part) and for itself (the share
/// of the managed heap allocated outside any process: the shell, the compositor, the OS services).
/// </param>
/// <param name="MeasuredProcesses">The number of processes whose memory is measured.</param>
public readonly record struct SystemMemoryUsage(
    long AnonymousBytes,
    long MappedBytes,
    long KernelBytes,
    int MeasuredProcesses)
{
    /// <summary>Gets the memory in use by measured processes and the OS.</summary>
    public long UsedBytes => AnonymousBytes + MappedBytes + KernelBytes;
}

/// <summary>What kind of object the OS holds memory for on behalf of a process (ADR 0068, section 1).</summary>
public enum KernelMemoryKind
{
    /// <summary>The in-memory buffer of a <c>KazoFileStream</c> opened for writing.</summary>
    StreamBuffer,

    /// <summary>Content a process put on the clipboard, held until replaced.</summary>
    Clipboard,

    /// <summary>Bytes waiting in a shell pipe, charged to the shell that created it.</summary>
    Pipe,

    /// <summary>The buffers of a network operation in flight.</summary>
    Socket
}

/// <summary>
/// Where the OS charges the memory it holds for processes (the "OS resources" layer of ADR 0068):
/// an OS service that buffers something for a process takes a <see cref="KernelMemoryCharge"/>,
/// resizes it as the buffer grows, and disposes it when the buffer is released.
/// </summary>
/// <remarks>
/// When memory is not measured, the ledger is the null ledger: every charge is
/// <see cref="KernelMemoryCharge.None"/> and costs nothing.
/// </remarks>
public interface IKernelMemoryLedger
{
    /// <summary>Gets whether charges are recorded (<see langword="false"/> for the null ledger).</summary>
    bool IsTracking { get; }

    /// <summary>Opens a charge against <paramref name="pid"/>.</summary>
    /// <param name="pid">The process the memory is held for.</param>
    /// <param name="kind">What the memory holds.</param>
    /// <param name="bytes">The initial size.</param>
    /// <returns>The charge; dispose it when the memory is released.</returns>
    KernelMemoryCharge Charge(ProcessId pid, KernelMemoryKind kind, long bytes = 0);
}

/// <summary>
/// One block of memory the OS holds for a process: resized as it grows or shrinks, released on
/// disposal. Thread-safe; resizing or disposing twice is harmless.
/// </summary>
public sealed class KernelMemoryCharge : IDisposable
{
    private readonly Action<long>? _apply;
    private readonly Lock _gate = new();
    private long _bytes;
    private bool _disposed;

    /// <summary>Creates a charge that reports each change of size to <paramref name="apply"/>.</summary>
    /// <param name="apply">Receives the signed difference of each change; <see langword="null"/> for a charge that records nothing.</param>
    /// <param name="bytes">The initial size.</param>
    public KernelMemoryCharge(Action<long>? apply, long bytes = 0)
    {
        _apply = apply;
        Resize(bytes);
    }

    /// <summary>Gets a charge that records nothing, returned by the null ledger.</summary>
    public static KernelMemoryCharge None { get; } = new(null);

    /// <summary>Gets the current size, <c>0</c> once disposed.</summary>
    public long Bytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>Changes the size of the held memory. Ignored once disposed; a negative size counts as zero.</summary>
    /// <param name="bytes">The new size.</param>
    public void Resize(long bytes)
    {
        if (_apply is null)
        {
            return;
        }

        long delta;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            bytes = Math.Max(0, bytes);
            delta = bytes - _bytes;
            _bytes = bytes;
        }

        if (delta != 0)
        {
            _apply(delta);
        }
    }

    /// <summary>Releases the held memory.</summary>
    public void Dispose()
    {
        long released;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            released = _bytes;
            _bytes = 0;
        }

        if (released != 0)
        {
            _apply?.Invoke(-released);
        }
    }
}
