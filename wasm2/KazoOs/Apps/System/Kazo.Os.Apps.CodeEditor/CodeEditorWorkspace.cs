using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Apps.CodeEditor;

/// <summary>One file of the open folder, as listed for search and quick open.</summary>
/// <param name="Path">The file's absolute path.</param>
/// <param name="RelativePath">The path from the folder's root, with <c>/</c> separators (<c>src/app.cs</c>).</param>
/// <param name="Length">The file's size in bytes.</param>
public sealed record CodeEditorWorkspaceFile(VirtualPath Path, string RelativePath, long Length)
{
    /// <summary>Gets the file's name (the last path segment).</summary>
    public string Name => RelativePath[(RelativePath.LastIndexOf('/') + 1)..];

    /// <summary>Gets the folder part of <see cref="RelativePath"/>, empty for a file at the root.</summary>
    public string Directory => RelativePath.LastIndexOf('/') is var slash and > 0 ? RelativePath[..slash] : string.Empty;
}

/// <summary>The files of the open folder, and whether the listing stopped at the limit.</summary>
public sealed record CodeEditorWorkspaceListing(IReadOnlyList<CodeEditorWorkspaceFile> Files, bool Truncated);

/// <summary>Result of creating a file or a folder in the open folder.</summary>
public sealed record CodeEditorCreateResult(bool Succeeded, VirtualPath? Path, string? Error = null);

/// <summary>
/// The folder opened in the editor (VS Code's workspace): lists its files for search and quick
/// open, and creates files and folders in it. Everything goes through the file-system gateway the
/// folder was opened with (a folder picked in the dialog carries its own access grant), so the
/// usual permissions apply and an unreadable folder is skipped, not an error.
/// </summary>
public sealed class CodeEditorWorkspace
{
    /// <summary>Folders never listed, as VS Code's default <c>files.exclude</c>/<c>search.exclude</c>.</summary>
    public static readonly IReadOnlySet<string> ExcludedDirectories =
        new HashSet<string>(StringComparer.Ordinal) { ".git", ".svn", ".hg", "node_modules", "bin", "obj" };

    /// <summary>Most files listed; beyond, the listing is marked truncated.</summary>
    public const int MaxFiles = 5000;

    private static readonly FileSystemPermissions DefaultFilePermissions = FileSystemPermissions.FromMode(0b110_100_100);
    private static readonly FileSystemPermissions DefaultDirectoryPermissions = FileSystemPermissions.FromMode(0b111_101_101);

    private readonly IAppFileSystemGateway _fileSystem;

    /// <summary>Creates the workspace of <paramref name="root"/>, read and written through <paramref name="fileSystem"/>.</summary>
    /// <param name="root">The open folder.</param>
    /// <param name="fileSystem">The gateway the folder is read and written with.</param>
    /// <param name="selectedResource">
    /// The grant of the folder dialog that picked <paramref name="root"/>, handed to the explorer's
    /// tree (the OS file control) so it lists the folder with the same grant; <see langword="null"/> otherwise.
    /// </param>
    public CodeEditorWorkspace(VirtualPath root, IAppFileSystemGateway fileSystem, FileSystemSelectedResourceHandle? selectedResource = null)
    {
        Root = root;
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        SelectedResource = selectedResource;
    }

    /// <summary>Gets the grant of the folder dialog that picked the folder, or <see langword="null"/>.</summary>
    public FileSystemSelectedResourceHandle? SelectedResource { get; }

    /// <summary>Gets the open folder.</summary>
    public VirtualPath Root { get; }

    /// <summary>Gets the gateway the folder was opened with.</summary>
    public IAppFileSystemGateway FileSystem => _fileSystem;

    /// <summary>Gets the folder's display name (its last segment, or <c>/</c>).</summary>
    public string Name => Root.Value == "/" ? "/" : Root.Value[(Root.Value.LastIndexOf('/') + 1)..];

    /// <summary>
    /// Lists every regular file under the folder, breadth first and sorted by path inside each
    /// folder, skipping <see cref="ExcludedDirectories"/> and folders it cannot read.
    /// </summary>
    public async Task<CodeEditorWorkspaceListing> ListFilesAsync(CancellationToken cancellationToken = default)
    {
        List<CodeEditorWorkspaceFile> files = [];
        Queue<VirtualPath> pending = new();
        pending.Enqueue(Root);
        while (pending.TryDequeue(out VirtualPath directory))
        {
            // ADR 0069: one checkpoint per folder of the walk.
            await KazoProcess.CheckpointAsync(cancellationToken);
            FileSystemResult<FileSystemDirectorySnapshot> listing = await _fileSystem.EnumerateAsync(
                new FileSystemEnumerateRequest(directory, FileSystemLinkBehavior.NoFollow), cancellationToken);
            if (!listing.Succeeded || listing.Value is null)
            {
                continue;
            }

            foreach (FileSystemDirectoryItem item in listing.Value.Entries)
            {
                VirtualPath path = Combine(directory, item.Name.Value);
                if (item.Metadata is DirectoryMetadata)
                {
                    if (!ExcludedDirectories.Contains(item.Name.Value))
                    {
                        pending.Enqueue(path);
                    }
                }
                else if (item.Metadata is FileMetadata metadata)
                {
                    if (files.Count >= MaxFiles)
                    {
                        return new CodeEditorWorkspaceListing(files, Truncated: true);
                    }

                    files.Add(new CodeEditorWorkspaceFile(path, RelativePathOf(path), metadata.Length));
                }
            }
        }

        return new CodeEditorWorkspaceListing(files, Truncated: false);
    }

    /// <summary>Creates an empty file named <paramref name="name"/> in <paramref name="directory"/>.</summary>
    public Task<CodeEditorCreateResult> CreateFileAsync(VirtualPath directory, string name, CancellationToken cancellationToken = default) =>
        CreateAsync(directory, name, FileSystemEntryKind.File, DefaultFilePermissions, cancellationToken);

    /// <summary>Creates a folder named <paramref name="name"/> in <paramref name="directory"/>.</summary>
    public Task<CodeEditorCreateResult> CreateDirectoryAsync(VirtualPath directory, string name, CancellationToken cancellationToken = default) =>
        CreateAsync(directory, name, FileSystemEntryKind.Directory, DefaultDirectoryPermissions, cancellationToken);

    /// <summary>
    /// Checks a name typed for a new file or folder: one path segment, not <c>.</c> or <c>..</c>.
    /// Returns the error to show, or null. VS Code also accepts <c>a/b.txt</c> (creating
    /// <c>a</c>); here nested paths are refused to keep one entry per creation.
    /// </summary>
    public static string? ValidateName(string? name)
    {
        string trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return Strings.Ambient["Workspace.NameRequired"];
        }

        if (trimmed is "." or "..")
        {
            return Strings.Ambient["Workspace.NameInvalid", trimmed];
        }

        if (trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.Any(char.IsControl))
        {
            return Strings.Ambient["Workspace.NameCharacters"];
        }

        return null;
    }

    /// <summary>Gets <paramref name="path"/> relative to the folder (<c>src/app.cs</c>), or the absolute path when outside it.</summary>
    public string RelativePathOf(VirtualPath path)
    {
        string root = Root.Value == "/" ? "/" : Root.Value + "/";
        return path.Value.StartsWith(root, StringComparison.Ordinal) ? path.Value[root.Length..] : path.Value;
    }

    /// <summary>Gets whether <paramref name="path"/> is the folder or inside it.</summary>
    public bool Contains(VirtualPath path) =>
        path == Root || path.Value.StartsWith(Root.Value == "/" ? "/" : Root.Value + "/", StringComparison.Ordinal);

    /// <summary>Joins a directory and an entry name.</summary>
    public static VirtualPath Combine(VirtualPath directory, string name) =>
        VirtualPath.Parse(directory.Value == "/" ? "/" + name : directory.Value + "/" + name);

    /// <summary>Gets the parent folder of <paramref name="path"/>.</summary>
    public static VirtualPath ParentOf(VirtualPath path)
    {
        int separator = path.Value.LastIndexOf('/');
        return separator <= 0 ? VirtualPath.Parse("/") : VirtualPath.Parse(path.Value[..separator]);
    }

    private async Task<CodeEditorCreateResult> CreateAsync(
        VirtualPath directory,
        string name,
        FileSystemEntryKind kind,
        FileSystemPermissions permissions,
        CancellationToken cancellationToken)
    {
        if (ValidateName(name) is { } invalid)
        {
            return new CodeEditorCreateResult(false, null, invalid);
        }

        VirtualPath path;
        try
        {
            path = Combine(directory, name.Trim());
        }
        catch (FormatException exception)
        {
            return new CodeEditorCreateResult(false, null, exception.Message);
        }

        FileSystemResult<FileSystemEntrySnapshot> existing = await _fileSystem.StatAsync(
            new FileSystemStatRequest(path, FileSystemLinkBehavior.NoFollow), cancellationToken);
        if (existing.Succeeded)
        {
            return new CodeEditorCreateResult(false, null, Strings.Ambient["Workspace.AlreadyExists", name.Trim()]);
        }

        FileSystemResult<FileSystemEntrySnapshot> parent = await _fileSystem.StatAsync(
            new FileSystemStatRequest(directory), cancellationToken);
        if (!parent.Succeeded || parent.Value is null)
        {
            return new CodeEditorCreateResult(false, null, Describe(parent.Error?.Code));
        }

        FileSystemMutationResult created = await _fileSystem.CreateAsync(
            new FileSystemCreateRequest(path, kind, permissions, parent.Value.Metadata.Revision),
            cancellationToken);
        return created.Succeeded
            ? new CodeEditorCreateResult(true, path)
            : new CodeEditorCreateResult(false, null, Describe(created.Transaction.Error?.Code));
    }

    private static string Describe(FileSystemErrorCode? code) => code switch
    {
        FileSystemErrorCode.PermissionDenied or FileSystemErrorCode.CapabilityDenied or FileSystemErrorCode.AuthorityDenied => Strings.Ambient["File.AccessDenied"],
        FileSystemErrorCode.NotFound => Strings.Ambient["Workspace.FolderGone"],
        FileSystemErrorCode.AlreadyExists => Strings.Ambient["Workspace.EntryExists"],
        FileSystemErrorCode.RevisionConflict => Strings.Ambient["Workspace.FolderChanged"],
        null => Strings.Ambient["Workspace.CreateFailed"],
        _ => code.ToString()!
    };
}
