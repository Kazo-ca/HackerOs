using System.IO.Compression;
using HackerOs.App.Abstractions;

namespace HackerOs.Apps.FileExplorer.Archives;

/// <summary>
/// The table of contents of one <c>.zip</c>, read once from its bytes (ADR 0185 §6-9): every folder and
/// file of the archive by its path inside it, with the folders a zip only implies (an entry
/// <c>a/b/c.txt</c> with no <c>a/</c> entry) added, as <c>unzip -l</c> would show them.
/// </summary>
/// <remarks>
/// Entry names are cleaned like an extraction would clean them: backslashes become slashes, empty and
/// <c>.</c> segments are dropped, and an entry that climbs out of the archive (<c>..</c>, the "zip slip")
/// or whose name is not a valid HackerOS path is left out instead of being shown.
/// </remarks>
internal sealed class ZipArchiveIndex
{
    private readonly byte[] _content;
    private readonly Dictionary<string, ZipArchiveNode> _nodes = new(StringComparer.Ordinal);

    private ZipArchiveIndex(byte[] content)
    {
        _content = content;
        _nodes[string.Empty] = ZipArchiveNode.Folder(string.Empty, null);
    }

    /// <summary>Gets the number of entries the index holds, folders included, the root excluded.</summary>
    public int Count => _nodes.Count - 1;

    /// <summary>Reads the table of contents of a <c>.zip</c>.</summary>
    /// <param name="content">The archive's bytes; kept to open its files later.</param>
    /// <returns>The index, or <see langword="null"/> when the bytes are not a readable zip.</returns>
    public static ZipArchiveIndex? TryRead(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        ZipArchiveIndex index = new(content);
        try
        {
            using ZipArchive archive = new(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                index.Add(entry);
            }
        }
        catch (InvalidDataException)
        {
            return null;
        }

        return index;
    }

    /// <summary>Normalizes a zip entry name to a path inside the archive (<c>a/b.txt</c>), or <see langword="null"/> when unsafe.</summary>
    internal static string? CleanEntryName(string fullName)
    {
        List<string> segments = [];
        foreach (string segment in fullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                return null;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            return null;
        }

        try
        {
            // The same rules as every HackerOS path (no control characters, at most 255 bytes a segment).
            return VirtualPath.Parse("/" + string.Join('/', segments)).Value[1..];
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Finds a folder or a file by its path inside the archive (<see cref="string.Empty"/> is the root).</summary>
    public bool TryGet(string entryPath, out ZipArchiveNode node) => _nodes.TryGetValue(entryPath, out node!);

    /// <summary>The children of a folder, in ordinal name order, as a directory listing gives them.</summary>
    public IReadOnlyList<ZipArchiveNode> ChildrenOf(ZipArchiveNode folder) =>
        [.. folder.Children.Select(name => _nodes[Combine(folder.Path, name)])];

    /// <summary>Inflates one file of the archive into memory.</summary>
    /// <param name="file">A file node of this index.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>A readable stream positioned at the beginning, owned by the caller.</returns>
    public async Task<MemoryStream> OpenFileAsync(ZipArchiveNode file, CancellationToken cancellationToken)
    {
        if (file.IsDirectory || file.EntryName is null)
        {
            throw new InvalidOperationException($"'{file.Path}' is a folder of the archive.");
        }

        using ZipArchive archive = new(new MemoryStream(_content, writable: false), ZipArchiveMode.Read);
        ZipArchiveEntry entry = archive.GetEntry(file.EntryName)
            ?? throw new InvalidDataException($"'{file.EntryName}' is missing from the archive.");
        MemoryStream buffer = new();
        await using (Stream inflated = entry.Open())
        {
            await inflated.CopyToAsync(buffer, cancellationToken);
        }

        buffer.Position = 0;
        return buffer;
    }

    private void Add(ZipArchiveEntry entry)
    {
        string? path = CleanEntryName(entry.FullName);
        if (path is null)
        {
            return;
        }

        bool isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
        ZipArchiveNode parent = EnsureFolder(ParentOf(path));
        if (_nodes.ContainsKey(path))
        {
            // A duplicate name (two entries, or a file over an implied folder): the first one stays.
            return;
        }

        DateTimeOffset modified = entry.LastWriteTime.ToUniversalTime();
        ZipArchiveNode node = isDirectory
            ? ZipArchiveNode.Folder(path, modified)
            : ZipArchiveNode.File(path, entry.FullName, entry.Length, modified);
        _nodes[path] = node;
        parent.Children.Add(node.Name);
    }

    private ZipArchiveNode EnsureFolder(string path)
    {
        if (_nodes.TryGetValue(path, out ZipArchiveNode? existing))
        {
            return existing;
        }

        ZipArchiveNode parent = EnsureFolder(ParentOf(path));
        ZipArchiveNode folder = ZipArchiveNode.Folder(path, null);
        _nodes[path] = folder;
        parent.Children.Add(folder.Name);
        return folder;
    }

    private static string ParentOf(string path)
    {
        int separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    /// <summary>Joins a folder of the archive and a child name.</summary>
    internal static string Combine(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;
}

/// <summary>One folder or file of a <see cref="ZipArchiveIndex"/>.</summary>
internal sealed class ZipArchiveNode
{
    private ZipArchiveNode(string path, bool isDirectory, string? entryName, long length, DateTimeOffset? modifiedUtc)
    {
        Path = path;
        IsDirectory = isDirectory;
        EntryName = entryName;
        Length = length;
        ModifiedUtc = modifiedUtc;
    }

    /// <summary>Gets the path inside the archive, without a leading slash; empty for the root.</summary>
    public string Path { get; }

    /// <summary>Gets the last segment of <see cref="Path"/>.</summary>
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    /// <summary>Gets whether this is a folder.</summary>
    public bool IsDirectory { get; }

    /// <summary>Gets the zip entry's own name, to open a file; <see langword="null"/> for a folder.</summary>
    public string? EntryName { get; }

    /// <summary>Gets the inflated size of a file; 0 for a folder.</summary>
    public long Length { get; }

    /// <summary>Gets the entry's modification time; <see langword="null"/> for a folder the zip only implies.</summary>
    public DateTimeOffset? ModifiedUtc { get; }

    /// <summary>Gets the names of a folder's children, in ordinal order.</summary>
    public SortedSet<string> Children { get; } = new(StringComparer.Ordinal);

    /// <summary>A folder of the archive.</summary>
    public static ZipArchiveNode Folder(string path, DateTimeOffset? modifiedUtc) => new(path, true, null, 0, modifiedUtc);

    /// <summary>A file of the archive.</summary>
    public static ZipArchiveNode File(string path, string entryName, long length, DateTimeOffset modifiedUtc) =>
        new(path, false, entryName, length, modifiedUtc);
}
