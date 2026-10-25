using HackerOs.AppSdk;

namespace HackerOs.Apps.FileExplorer.FileControl;

/// <summary>
/// Describes one sortable column of the <see cref="FileViewMode.Details"/> renderer. The default column
/// set (Name, Kind, Size, Modified) matches <c>FileExplorerState</c>'s existing sort keys so migrating
/// <c>FileExplorerWindow</c> onto <see cref="FileView"/> is behavior-preserving; see
/// <c>doc/wasm/dev/old/Global-FileView-And-MessagingSystem/integrationPlan.md</c> (<c>FV-002</c>).
/// </summary>
/// <param name="Key">Stable column identifier, used as the sort key and for host persistence.</param>
/// <param name="Header">Display header text.</param>
/// <param name="SortAccessor">Extracts the comparable sort value from one item.</param>
/// <param name="Width">Optional fixed column width in pixels; unset columns share remaining space.</param>
public sealed record FileViewColumn(
    string Key,
    string Header,
    Func<FileViewItem, IComparable> SortAccessor,
    double? Width = null);
