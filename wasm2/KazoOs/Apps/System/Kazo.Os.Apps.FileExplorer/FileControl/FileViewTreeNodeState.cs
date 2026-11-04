using Kazo.Os.AppSdk;

namespace Kazo.Os.Apps.FileExplorer.FileControl;

/// <summary>
/// Per-node expansion/children cache for <see cref="FileViewMode.Tree"/>, keyed by path (in
/// <see cref="FileViewTree"/>'s node cache) so state survives the owning <see cref="FileView"/> rebuilding
/// its <see cref="FileViewItem"/> instances on every navigate/refresh. Children fetched here for an
/// expanded node are not part of <see cref="FileView.Items"/> and are not kept live by the directory
/// watch subscription — only the root level (mirroring <see cref="FileView.Items"/>) is (<c>FV-004</c>).
/// </summary>
public sealed class FileViewTreeNodeState
{
    /// <summary>The item this node displays. Reassigned in place across rebuilds so state is preserved by path.</summary>
    public FileViewItem Item { get; set; } = null!;

    /// <summary>Whether this node's children are currently shown.</summary>
    public bool IsExpanded { get; set; }

    /// <summary>Whether <see cref="Children"/> is currently being fetched.</summary>
    public bool IsLoading { get; set; }

    /// <summary><see langword="null"/> until first expanded; populated once via <see cref="Simulation.Abstractions.Gateways.IAppFileSystemGateway.EnumerateAsync"/>.</summary>
    public List<FileViewTreeNodeState>? Children { get; set; }
}
