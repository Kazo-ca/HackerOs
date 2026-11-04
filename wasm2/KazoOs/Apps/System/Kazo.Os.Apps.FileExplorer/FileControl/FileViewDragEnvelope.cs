using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.DragDrop;

namespace Kazo.Os.Apps.FileExplorer.FileControl;

/// <summary>
/// Wire envelope for <see cref="FileView"/>'s drag-drop payload (<c>FV-006</c>), serialized as JSON under
/// the <c>application/vnd.hackeros.file-drag+json</c> <c>DataTransfer</c> MIME type. A dragged selection
/// may mix files and folders, but <see cref="VirtualFileDragPayload"/>/<see cref="VirtualFolderDragPayload"/>
/// are separate record types with no shared base, hence the two lists here.
/// </summary>
internal sealed record FileViewDragEnvelope(
    IReadOnlyList<VirtualFileDragPayload> Files,
    IReadOnlyList<VirtualFolderDragPayload> Folders);
