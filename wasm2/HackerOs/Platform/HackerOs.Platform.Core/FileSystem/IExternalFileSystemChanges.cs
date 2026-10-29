using HackerOs.App.Abstractions;

namespace HackerOs.Platform.Core.FileSystem;

/// <summary>
/// A file service whose folders can change outside the OS (a network drive changed on the host
/// computer, ADR 0185 §15): while a folder is tracked, the changes found there are published as the
/// OS's own writes are, on the folder's change topic.
/// </summary>
public interface IExternalFileSystemChanges
{
    /// <summary>Starts finding outside changes in <paramref name="directory"/>, until the result is disposed.</summary>
    /// <param name="directory">The folder a watcher subscribed to.</param>
    /// <returns>The handle that stops tracking, or <see langword="null"/> when the folder cannot change outside the OS.</returns>
    IDisposable? TrackExternalChanges(VirtualPath directory);
}
