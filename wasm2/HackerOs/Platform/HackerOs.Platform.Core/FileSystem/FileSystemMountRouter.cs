using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.FileSystem;

namespace HackerOs.Platform.Core.FileSystem;

/// <summary>Routes paths to the most-specific provider mount on a segment boundary.</summary>
/// <remarks>
/// The mounts given to the constructor are fixed; <see cref="Mount"/> and <see cref="Unmount"/> add
/// and remove the network drives of ADR 0181 while the OS runs. Both swap the tables in one
/// assignment, so a concurrent <see cref="Resolve"/> sees either the old or the new table.
/// </remarks>
public sealed class FileSystemMountRouter : IDynamicFileSystemMountRouter
{
    private readonly HashSet<string> _fixedPaths;
    private readonly Lock _gate = new();
    private Tables _tables;

    /// <summary>Initializes a router with one root provider and optional projected mounts.</summary>
    public FileSystemMountRouter(
        IFileSystemProvider rootProvider,
        IEnumerable<FileSystemMount>? mounts = null)
    {
        ArgumentNullException.ThrowIfNull(rootProvider);
        FileSystemMount root = new(VirtualPath.Parse("/"), rootProvider, isProtectedRoot: true);
        FileSystemMount[] supplied = mounts?.ToArray() ?? [];

        if (supplied.Any(static mount => mount.Path.Value == "/"))
        {
            throw new ArgumentException("The root provider is registered separately.", nameof(mounts));
        }

        FileSystemMount[] all = [root, .. supplied];
        string? duplicatePath = all
            .GroupBy(static mount => mount.Path.Value, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1)
            ?.Key;
        if (duplicatePath is not null)
        {
            throw new ArgumentException($"Duplicate filesystem mount path '{duplicatePath}'.", nameof(mounts));
        }

        _fixedPaths = new HashSet<string>(all.Select(static mount => mount.Path.Value), StringComparer.Ordinal);
        _tables = new Tables(all);
    }

    /// <inheritdoc />
    public event Action? MountsChanged;

    /// <inheritdoc />
    public IReadOnlyList<FileSystemMount> Mounts => _tables.Ordered;

    /// <inheritdoc />
    public FileSystemMountResolution Resolve(VirtualPath path)
    {
        if (string.IsNullOrEmpty(path.Value))
        {
            throw new ArgumentException("Mount routing requires a canonical path.", nameof(path));
        }

        FileSystemMount mount = _tables.BySpecificity.First(candidate => Matches(path, candidate.Path));
        return new FileSystemMountResolution(mount, path);
    }

    /// <inheritdoc />
    public void Mount(FileSystemMount mount)
    {
        ArgumentNullException.ThrowIfNull(mount);
        lock (_gate)
        {
            FileSystemMount owner = Resolve(mount.Path).Mount;
            if (owner.Path == mount.Path)
            {
                throw new InvalidOperationException($"'{mount.Path}' is already a mount point.");
            }

            if (owner.Path.Value != "/")
            {
                throw new InvalidOperationException($"'{mount.Path}' is inside the mount '{owner.Path}'.");
            }

            if (_tables.Ordered.Any(existing => existing.Path.Value.StartsWith($"{mount.Path.Value}/", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"'{mount.Path}' contains another mount.");
            }

            _tables = new Tables([.. _tables.Ordered, mount]);
        }

        MountsChanged?.Invoke();
    }

    /// <inheritdoc />
    public bool Unmount(VirtualPath path)
    {
        lock (_gate)
        {
            if (_fixedPaths.Contains(path.Value))
            {
                throw new InvalidOperationException($"'{path}' is a fixed mount.");
            }

            FileSystemMount[] remaining = [.. _tables.Ordered.Where(mount => mount.Path != path)];
            if (remaining.Length == _tables.Ordered.Count)
            {
                return false;
            }

            _tables = new Tables(remaining);
        }

        MountsChanged?.Invoke();
        return true;
    }

    private static bool Matches(VirtualPath path, VirtualPath mountPath) =>
        mountPath.Value == "/"
        || path == mountPath
        || path.Value.StartsWith($"{mountPath.Value}/", StringComparison.Ordinal);

    /// <summary>The mounts, least specific first for listing and most specific first for routing.</summary>
    private sealed class Tables
    {
        public Tables(IEnumerable<FileSystemMount> mounts)
        {
            FileSystemMount[] all = [.. mounts];
            Ordered = Array.AsReadOnly(all
                .OrderBy(static mount => mount.Path.Value.Length)
                .ThenBy(static mount => mount.Path.Value, StringComparer.Ordinal)
                .ToArray());
            BySpecificity = all
                .OrderByDescending(static mount => mount.Path.Value.Length)
                .ThenBy(static mount => mount.Path.Value, StringComparer.Ordinal)
                .ToArray();
        }

        public IReadOnlyList<FileSystemMount> Ordered { get; }

        public FileSystemMount[] BySpecificity { get; }
    }
}
