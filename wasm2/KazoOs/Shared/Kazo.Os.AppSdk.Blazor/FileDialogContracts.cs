using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.AppSdk.Blazor;

/// <summary>
/// Defines one selectable group of virtual files.
/// </summary>
/// <param name="DisplayName">Label shown in the dialog filter selector.</param>
/// <param name="Extensions">Normalized extensions including the leading dot.</param>
/// <param name="MediaTypes">Media types accepted by the filter.</param>
public sealed record FileDialogFilter(
    string DisplayName,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> MediaTypes);

/// <summary>
/// Defines access requested for files selected by the user.
/// </summary>
public enum SelectedFileAccess
{
    /// <summary>The app requests read access only.</summary>
    Read,

    /// <summary>The app requests read and write access.</summary>
    ReadWrite
}

/// <summary>
/// Configures the ecosystem-owned file-open dialog.
/// </summary>
public sealed record OpenFileDialogRequest
{
    /// <summary>Gets the optional dialog title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the initial virtual directory.</summary>
    public VirtualPath? InitialDirectory { get; init; }

    /// <summary>Gets selectable file filters.</summary>
    public IReadOnlyList<FileDialogFilter> Filters { get; init; } = [];

    /// <summary>Gets whether the user may select multiple files.</summary>
    public bool AllowMultiple { get; init; }

    /// <summary>Gets the access requested for selected files.</summary>
    public SelectedFileAccess RequestedAccess { get; init; } = SelectedFileAccess.Read;
}

/// <summary>
/// Configures the ecosystem-owned file-save dialog.
/// </summary>
public sealed record SaveFileDialogRequest
{
    /// <summary>Gets the optional dialog title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the initial virtual directory.</summary>
    public VirtualPath? InitialDirectory { get; init; }

    /// <summary>Gets the suggested file name.</summary>
    public string? SuggestedFileName { get; init; }

    /// <summary>Gets the normalized default extension including the leading dot.</summary>
    public string? DefaultExtension { get; init; }

    /// <summary>Gets selectable file filters.</summary>
    public IReadOnlyList<FileDialogFilter> Filters { get; init; } = [];

    /// <summary>Gets whether the dialog must confirm replacement of an existing file.</summary>
    public bool ConfirmOverwrite { get; init; } = true;
}

/// <summary>
/// Configures the ecosystem-owned folder-selection dialog.
/// </summary>
public sealed record SelectFolderDialogRequest
{
    /// <summary>Gets the optional dialog title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the initial virtual directory.</summary>
    public VirtualPath? InitialDirectory { get; init; }

    /// <summary>Gets whether the dialog may create a new folder when policy permits.</summary>
    public bool AllowCreateFolder { get; init; }
}

/// <summary>
/// Describes whether the user selected a value or cancelled a dialog.
/// </summary>
public enum FileDialogStatus
{
    /// <summary>The user selected one or more valid virtual paths.</summary>
    Selected,

    /// <summary>The user cancelled without selecting a path.</summary>
    Cancelled
}

/// <summary>Combines a selected file path with its short-lived delegated authority.</summary>
public sealed record SelectedFileResource(
    VirtualPath Path,
    FileSystemSelectedResourceHandle Handle);

/// <summary>Combines a selected folder path with its short-lived delegated authority.</summary>
public sealed record SelectedFolderResource(
    VirtualPath Path,
    FileSystemSelectedResourceHandle Handle);

/// <summary>
/// Contains the result of a file-open dialog.
/// </summary>
/// <param name="Status">Dialog outcome.</param>
/// <param name="Resources">Selected resources, empty when cancelled.</param>
public sealed record OpenFileDialogResult(
    FileDialogStatus Status,
    IReadOnlyList<SelectedFileResource> Resources);

/// <summary>
/// Contains the result of a file-save dialog.
/// </summary>
/// <param name="Status">Dialog outcome.</param>
/// <param name="Resource">Selected destination, or no resource when cancelled.</param>
public sealed record SaveFileDialogResult(
    FileDialogStatus Status,
    SelectedFileResource? Resource);

/// <summary>
/// Contains the result of a folder-selection dialog.
/// </summary>
/// <param name="Status">Dialog outcome.</param>
/// <param name="Resource">Selected folder, or no resource when cancelled.</param>
public sealed record SelectFolderDialogResult(
    FileDialogStatus Status,
    SelectedFolderResource? Resource);

/// <summary>
/// Displays modal virtual filesystem dialogs on behalf of a window app.
/// </summary>
public interface IFileDialogService
{
    /// <summary>Displays a file-open dialog scoped to the requesting app instance.</summary>
    ValueTask<OpenFileDialogResult> OpenFileAsync(
        IAppExecutionContext context,
        OpenFileDialogRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Displays a file-save dialog scoped to the requesting app instance.</summary>
    ValueTask<SaveFileDialogResult> SaveFileAsync(
        IAppExecutionContext context,
        SaveFileDialogRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Displays a folder-selection dialog scoped to the requesting app instance.</summary>
    ValueTask<SelectFolderDialogResult> SelectFolderAsync(
        IAppExecutionContext context,
        SelectFolderDialogRequest request,
        CancellationToken cancellationToken = default);
}