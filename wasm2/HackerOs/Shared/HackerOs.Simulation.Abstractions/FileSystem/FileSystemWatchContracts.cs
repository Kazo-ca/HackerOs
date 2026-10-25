using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.Events;

namespace HackerOs.Simulation.Abstractions.FileSystem;

/// <summary>
/// Filesystem-watch contracts, per <c>doc/wasm/dev/old/Global-FileView-And-MessagingSystem/MessagingSystem.md</c>
/// (<c>MSG-011</c> through <c>MSG-015</c>). Built entirely on <see cref="ITopicMessageBus"/> — no
/// separate watch mechanism.
/// </summary>
public enum FileSystemChangeKind
{
    /// <summary>An entry was created.</summary>
    Created,

    /// <summary>A regular file's content changed.</summary>
    ContentModified,

    /// <summary>An entry's metadata (permissions, owner/group) changed.</summary>
    MetadataModified,

    /// <summary>An entry was deleted.</summary>
    Deleted,

    /// <summary>An entry moved away from the watched path; see <see cref="FileSystemChangeEvent.MovedToPath"/>.</summary>
    MovedFrom,

    /// <summary>An entry moved into the watched path.</summary>
    MovedTo
}

/// <summary>Controls how much of a directory subtree a watch subscription observes.</summary>
public enum FileSystemWatchScope
{
    /// <summary>
    /// Only the watched entry itself. Not implemented in this pass — <c>AppFileSystemWatchGateway</c>
    /// throws <see cref="NotSupportedException"/>; <c>FileView</c>'s only real consumer only ever needs
    /// <see cref="ImmediateChildren"/>. An intentional, honest scope reduction, not an oversight.
    /// </summary>
    ThisEntry,

    /// <summary>The watched directory's immediate children only.</summary>
    ImmediateChildren,

    /// <summary>
    /// The complete subtree. Not implemented in this pass — <c>AppFileSystemWatchGateway</c> throws
    /// <see cref="NotSupportedException"/>, as documented since the original design (see
    /// MessagingSystem.md).
    /// </summary>
    Recursive
}

/// <summary>One filesystem change notification.</summary>
/// <param name="Path">Canonical path of the changed entry.</param>
/// <param name="Kind">Kind of change observed.</param>
/// <param name="EntryKind">Kind of the changed entry.</param>
/// <param name="Revision">Revision of the entry (or its parent, for <see cref="FileSystemChangeKind.Deleted"/>) after the change.</param>
/// <param name="OccurredAtUtc">Time the change was observed.</param>
/// <param name="MovedToPath">Destination path, set only for <see cref="FileSystemChangeKind.MovedFrom"/>.</param>
public sealed record FileSystemChangeEvent(
    VirtualPath Path,
    FileSystemChangeKind Kind,
    FileSystemEntryKind EntryKind,
    long Revision,
    DateTimeOffset OccurredAtUtc,
    VirtualPath? MovedToPath = null);

/// <summary>
/// Builds the well-known shared topic a directory's changes are published on, so the filesystem provider
/// and every watcher agree on the topic name by construction rather than by convention.
/// </summary>
public static class FileSystemTopics
{
    /// <summary>Gets the topic <paramref name="path"/>'s changes are published on.</summary>
    /// <remarks>
    /// Each of <paramref name="path"/>'s segments is lowercase-hex-encoded into its own topic segment
    /// (never a single hashed blob) so the topic naturally mirrors the path's hierarchy — useful should a
    /// future pass add <see cref="FileSystemWatchScope.Recursive"/> support keyed on topic-prefix
    /// matching — while still satisfying <c>TopicNameBuilder.Segment</c>'s kebab-case-only grammar for
    /// arbitrary Unicode file/directory names (spaces, uppercase, punctuation, non-Latin scripts, ...).
    /// </remarks>
    public static TopicName ForDirectory(VirtualPath path)
    {
        TopicNameBuilder builder = TopicNames.Shared("filesystem").Segment("changed");
        foreach (string segment in path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            builder = builder.Segment(Convert.ToHexStringLower(Encoding.UTF8.GetBytes(segment)));
        }

        return builder.Build();
    }
}

/// <summary>
/// Provides one app instance's authorized directory-change watch access, reusing the same
/// filesystem-read capability/constraint already required to read the watched path — watching a
/// directory never reveals more than reading it already would.
/// </summary>
public interface IAppFileSystemWatchGateway
{
    /// <summary>
    /// Starts watching <paramref name="path"/>. Disposing the returned subscription stops delivery and
    /// completes its channel.
    /// </summary>
    /// <exception cref="Gateways.AppGatewayAccessDeniedException">
    /// The caller cannot currently read <paramref name="path"/>.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="scope"/> is <see cref="FileSystemWatchScope.ThisEntry"/> or
    /// <see cref="FileSystemWatchScope.Recursive"/> — not implemented in this pass.
    /// </exception>
    ValueTask<ITopicChannelSubscription<FileSystemChangeEvent>> WatchAsync(
        VirtualPath path, FileSystemWatchScope scope, CancellationToken cancellationToken = default);
}
