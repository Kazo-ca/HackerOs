using HackerOs.Simulation.Abstractions.FileSystem;

namespace HackerOs.Platform.Core.Tests.FileSystem.HostFolders;

/// <summary>An in-memory folder of the host computer, with the browser's error behaviour.</summary>
internal sealed class FakeHostFolder(string name) : IHostFolder
{
    private readonly Node _root = new(name, null);
    private long _clock = 1_700_000_000_000;

    public string Name { get; } = name;

    public HostFolderPermission Permission { get; set; } = HostFolderPermission.Granted;

    public HostFolderPermission PermissionAfterRequest { get; set; } = HostFolderPermission.Granted;

    public bool CanMove { get; set; } = true;

    public bool Disposed { get; private set; }

    public ValueTask<HostFolderPermission> QueryPermissionAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Permission);

    public ValueTask<HostFolderPermission> RequestPermissionAsync(CancellationToken cancellationToken = default)
    {
        Permission = PermissionAfterRequest;
        return ValueTask.FromResult(Permission);
    }

    public ValueTask<HostFolderEntry> StatAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Describe(Find(path)));

    public ValueTask<IReadOnlyList<HostFolderEntry>> ListAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default)
    {
        Node folder = Folder(path);
        return ValueTask.FromResult<IReadOnlyList<HostFolderEntry>>([.. folder.Children!.Values.Select(Describe)]);
    }

    public ValueTask<byte[]> ReadAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default)
    {
        Node node = Find(path);
        return node.Content is { } content
            ? ValueTask.FromResult(content.ToArray())
            : throw new HostFolderException(HostFolderErrorKind.TypeMismatch, "not a file");
    }

    public ValueTask CreateAsync(IReadOnlyList<string> path, HostFolderEntryKind kind, CancellationToken cancellationToken = default)
    {
        Node parent = Folder(path.Take(path.Count - 1).ToArray());
        string entry = path[^1];
        if (parent.Children!.ContainsKey(entry))
        {
            throw new HostFolderException(HostFolderErrorKind.AlreadyExists, $"{entry} exists");
        }

        parent.Children[entry] = kind == HostFolderEntryKind.Directory
            ? new Node(entry, null)
            : new Node(entry, []) { LastModified = ++_clock };
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(IReadOnlyList<string> path, byte[] content, CancellationToken cancellationToken = default)
    {
        Node node = Find(path);
        if (node.Content is null)
        {
            throw new HostFolderException(HostFolderErrorKind.TypeMismatch, "not a file");
        }

        node.Content = [.. content];
        node.LastModified = ++_clock;
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(IReadOnlyList<string> path, bool recursive, CancellationToken cancellationToken = default)
    {
        Node parent = Folder(path.Take(path.Count - 1).ToArray());
        Node node = Find(path);
        if (node.Children is { Count: > 0 } && !recursive)
        {
            throw new HostFolderException(HostFolderErrorKind.NotEmpty, "not empty");
        }

        parent.Children!.Remove(path[^1]);
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> TryMoveAsync(
        IReadOnlyList<string> source,
        IReadOnlyList<string> destination,
        CancellationToken cancellationToken = default)
    {
        if (!CanMove)
        {
            return ValueTask.FromResult(false);
        }

        Node node = Find(source);
        Node from = Folder(source.Take(source.Count - 1).ToArray());
        Node to = Folder(destination.Take(destination.Count - 1).ToArray());
        from.Children!.Remove(source[^1]);
        node.Name = destination[^1];
        to.Children![node.Name] = node;
        return ValueTask.FromResult(true);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>Adds a file the user put in the folder outside HackerOS.</summary>
    public void Put(string text, params string[] path)
    {
        Node parent = _root;
        foreach (string segment in path[..^1])
        {
            if (!parent.Children!.TryGetValue(segment, out Node? child))
            {
                child = new Node(segment, null);
                parent.Children[segment] = child;
            }

            parent = child;
        }

        parent.Children![path[^1]] = new Node(path[^1], [.. System.Text.Encoding.UTF8.GetBytes(text)]) { LastModified = ++_clock };
    }

    /// <summary>The text of a file, or <see langword="null"/> when there is none.</summary>
    public string? Text(params string[] path)
    {
        try
        {
            return Find(path).Content is { } content ? System.Text.Encoding.UTF8.GetString(content.ToArray()) : null;
        }
        catch (HostFolderException)
        {
            return null;
        }
    }

    public bool Exists(params string[] path) => Text(path) is not null || TryFolder(path);

    private bool TryFolder(string[] path)
    {
        try
        {
            return Find(path).Children is not null;
        }
        catch (HostFolderException)
        {
            return false;
        }
    }

    private Node Find(IReadOnlyList<string> path)
    {
        if (Permission != HostFolderPermission.Granted)
        {
            throw new HostFolderException(HostFolderErrorKind.NotAllowed, "permission");
        }

        Node current = _root;
        foreach (string segment in path)
        {
            if (current.Children is null)
            {
                throw new HostFolderException(HostFolderErrorKind.TypeMismatch, "not a folder");
            }

            if (!current.Children.TryGetValue(segment, out Node? child))
            {
                throw new HostFolderException(HostFolderErrorKind.NotFound, $"{segment} not found");
            }

            current = child;
        }

        return current;
    }

    private Node Folder(IReadOnlyList<string> path)
    {
        Node node = Find(path);
        return node.Children is null ? throw new HostFolderException(HostFolderErrorKind.TypeMismatch, "not a folder") : node;
    }

    private static HostFolderEntry Describe(Node node) => node.Content is { } content
        ? new HostFolderEntry(node.Name, HostFolderEntryKind.File, content.Count, DateTimeOffset.FromUnixTimeMilliseconds(node.LastModified))
        : new HostFolderEntry(node.Name, HostFolderEntryKind.Directory, 0, null);

    private sealed class Node(string name, List<byte>? content)
    {
        public string Name { get; set; } = name;

        public List<byte>? Content { get; set; } = content;

        public Dictionary<string, Node>? Children { get; } = content is null ? new(StringComparer.Ordinal) : null;

        public long LastModified { get; set; }
    }
}

/// <summary>The browser's folder dialog and handle store, in memory.</summary>
internal sealed class FakeHostFolderAccess : IHostFolderAccess
{
    private readonly Dictionary<string, FakeHostFolder> _stored = new(StringComparer.Ordinal);
    private int _nextKey = 1;

    public bool Supported { get; set; } = true;

    /// <summary>The folder the next dialog returns; <see langword="null"/> closes the dialog.</summary>
    public FakeHostFolder? NextPick { get; set; }

    public bool RequireGesture { get; set; }

    public IReadOnlyCollection<string> StoredKeys => _stored.Keys;

    public FakeHostFolder Stored(string key) => _stored[key];

    public ValueTask<bool> IsSupportedAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Supported);

    public ValueTask<HostFolderPick?> PickAsync(CancellationToken cancellationToken = default)
    {
        if (!Supported)
        {
            throw new NotSupportedException("no dialog");
        }

        if (RequireGesture)
        {
            throw new InvalidOperationException("user activation required");
        }

        if (NextPick is not { } folder)
        {
            return ValueTask.FromResult<HostFolderPick?>(null);
        }

        string key = $"key-{_nextKey++}";
        _stored[key] = folder;
        return ValueTask.FromResult<HostFolderPick?>(new HostFolderPick(key, folder));
    }

    public ValueTask<IHostFolder?> LoadAsync(string key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IHostFolder?>(_stored.TryGetValue(key, out FakeHostFolder? folder) ? folder : null);

    public ValueTask ForgetAsync(string key, CancellationToken cancellationToken = default)
    {
        _stored.Remove(key);
        return ValueTask.CompletedTask;
    }
}
