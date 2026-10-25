using HackerOs.AppSdk;

namespace HackerOs.Apps.FileExplorer.FileControl;

/// <summary>
/// Selects which renderer <see cref="FileView"/> uses to display its current directory's contents. All
/// three modes share the same <see cref="FileViewItem"/> data and selection state — switching modes
/// never loses selection. See <c>doc/wasm/dev/old/Global-FileView-And-MessagingSystem/FileViewControl.md</c>.
/// </summary>
public enum FileViewMode
{
    /// <summary>Lazily expandable hierarchical directory tree.</summary>
    Tree,

    /// <summary>Sortable table with per-column headers (Name, Kind, Size, Modified by default).</summary>
    Details,

    /// <summary>Wrapping grid of icon tiles.</summary>
    Icons,

    /// <summary>
    /// The desktop grid (ADR 0152 §2, ADR 0160): one icon per cell, kept where the user put it, with
    /// the Home and Trash system icons. Only drawn with the File Explorer's desktop binding.
    /// </summary>
    Desktop
}
