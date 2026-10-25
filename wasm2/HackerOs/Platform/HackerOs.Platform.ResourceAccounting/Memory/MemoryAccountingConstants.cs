namespace HackerOs.Platform.ResourceAccounting.Memory;

/// <summary>
/// The calculation rules of ADR 0068, section 1, documented in <c>doc/wasm/dev/system-metrics.md</c> and
/// covered by tests. Changing one changes what <c>/proc</c> shows: update both.
/// </summary>
public static class MemoryAccountingConstants
{
    /// <summary>
    /// Half-life of a process's weight in the split of the resident heap: allocations made this long
    /// ago weigh half as much as fresh ones. Ten seconds keeps a process that just built a large
    /// document heavy for a while, and lets one that went idle fade within a minute (six half-lives,
    /// under 2 %), like pages aging out of a working set.
    /// </summary>
    public static readonly TimeSpan ResidentHalfLife = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Fixed kernel cost of one open window, in bytes: 512 KiB, standing for what a window manager and
    /// compositor keep per window (surface, render tree, DOM nodes) and that no allocation counter
    /// sees, since the rendering runs outside the app's frame.
    /// </summary>
    public const long WindowBytes = 512 * 1024;
}
