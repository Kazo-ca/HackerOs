namespace HackerOs.AppSdk.IO;

/// <summary>The total size of a folder's content (ADR 0185 §5), from <see cref="HackDirectory.GetTotalSizeAsync"/>.</summary>
/// <param name="Length">Sum of the files' lengths, in bytes.</param>
/// <param name="FileCount">Number of files counted.</param>
/// <param name="DirectoryCount">Number of folders below the measured one.</param>
/// <param name="IsComplete"><see langword="false"/> when a folder below could not be read: the totals cover the rest.</param>
public sealed record HackDirectorySize(long Length, long FileCount, long DirectoryCount, bool IsComplete);
