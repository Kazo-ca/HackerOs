namespace Kazo.Os.Platform.Core.Diagnostics;

/// <summary>
/// Fixed size and retention limits for every file under <c>/var/log</c> (ADR 0045, section 3):
/// one active file of at most <see cref="MaxFileBytes"/> plus <see cref="MaxArchives"/> numbered
/// archives (<c>.1</c> newest to <c>.5</c> oldest), rotated the way <c>logrotate</c> does.
/// </summary>
/// <remarks>
/// These are constants, not settings, in phase 1 of the rebuild plan (section 11 defaults).
/// A single log therefore never occupies more than six times <see cref="MaxFileBytes"/>.
/// </remarks>
public static class LogRotationPolicy
{
    /// <summary>Maximum size of an active log file before the next batch triggers a rotation: 1 MiB.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    /// <summary>Number of uncompressed archives kept per log; the oldest is deleted on rotation.</summary>
    public const int MaxArchives = 5;

    /// <summary>
    /// Returns whether appending <paramref name="batchBytes"/> to a file currently holding
    /// <paramref name="currentBytes"/> must rotate first. An empty file never rotates, so a batch
    /// larger than the limit is still written whole into a fresh file and no line is ever cut.
    /// </summary>
    /// <param name="currentBytes">Current length of the active file.</param>
    /// <param name="batchBytes">UTF-8 length of the batch about to be appended.</param>
    public static bool ShouldRotate(long currentBytes, long batchBytes) =>
        currentBytes > 0 && currentBytes + batchBytes > MaxFileBytes;
}
