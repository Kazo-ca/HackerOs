namespace Kazo.Os.AppSdk.DragDrop;

/// <summary>
/// Strongly typed drag payload for virtual files.
/// </summary>
public sealed record VirtualFileDragPayload(
    string SourceAppId,
    string FilePath,
    string FileName,
    long SizeBytes,
    string? MimeType
);

/// <summary>
/// Strongly typed drag payload for virtual folders.
/// </summary>
public sealed record VirtualFolderDragPayload(
    string SourceAppId,
    string FolderPath,
    string FolderName,
    int ItemCount
);
