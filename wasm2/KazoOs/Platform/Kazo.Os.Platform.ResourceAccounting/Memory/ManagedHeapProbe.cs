namespace Kazo.Os.Platform.ResourceAccounting.Memory;

/// <summary>
/// What the memory measurement reads from the .NET garbage collector (ADR 0068). An interface so the
/// tests can drive allocations, collections and heap sizes deterministically.
/// </summary>
public interface IManagedHeapProbe
{
    /// <summary>Gets the managed bytes allocated so far by the current thread (monotonic).</summary>
    long GetAllocatedBytesForCurrentThread();

    /// <summary>
    /// Gets the number of generation-0 collections so far. WebAssembly has no
    /// <c>GC.RegisterForFullGCNotification</c>: a change of this count is how the measurement learns
    /// that a collection happened.
    /// </summary>
    int CollectionCount { get; }

    /// <summary>Gets the size of the managed heap after the last collection, in bytes.</summary>
    long HeapSizeBytes { get; }
}

/// <summary>The real probe, over <see cref="GC"/>.</summary>
public sealed class GcManagedHeapProbe : IManagedHeapProbe
{
    /// <summary>Gets the shared instance (the probe is stateless).</summary>
    public static GcManagedHeapProbe Instance { get; } = new();

    private GcManagedHeapProbe()
    {
    }

    /// <inheritdoc />
    public long GetAllocatedBytesForCurrentThread() => GC.GetAllocatedBytesForCurrentThread();

    /// <inheritdoc />
    public int CollectionCount => GC.CollectionCount(0);

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="GCMemoryInfo.HeapSizeBytes"/> when the runtime reports it; otherwise (a runtime that
    /// returns zero) <see cref="GC.GetTotalMemory(bool)"/>, the bytes currently thought allocated.
    /// </remarks>
    public long HeapSizeBytes
    {
        get
        {
            long heap = GC.GetGCMemoryInfo().HeapSizeBytes;
            return heap > 0 ? heap : GC.GetTotalMemory(forceFullCollection: false);
        }
    }
}
