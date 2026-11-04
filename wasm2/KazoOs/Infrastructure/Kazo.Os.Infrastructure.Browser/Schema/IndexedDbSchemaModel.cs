namespace Kazo.Os.Infrastructure.Browser.Schema;

/// <summary>
/// Declares one secondary index on an <see cref="IndexedDbObjectStoreDefinition"/>, mirroring the
/// options accepted by <c>IDBObjectStore.createIndex</c>.
/// </summary>
public sealed record IndexedDbIndexDefinition
{
    /// <summary>Initializes a validated index declaration.</summary>
    /// <param name="name">Non-empty index name, unique within its owning object store.</param>
    /// <param name="keyPath">One or more non-empty property-path segments the index is built from.</param>
    /// <param name="unique">Whether the browser rejects a second record with the same index key.</param>
    /// <param name="multiEntry">
    /// Whether an array-valued key path contributes one index entry per array element instead of
    /// one entry for the whole array.
    /// </param>
    /// <exception cref="ArgumentException">The name is empty or no key-path segment is supplied.</exception>
    public IndexedDbIndexDefinition(
        string name,
        IReadOnlyList<string> keyPath,
        bool unique = false,
        bool multiEntry = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (keyPath is not { Count: > 0 })
        {
            throw new ArgumentException("An index requires at least one key-path segment.", nameof(keyPath));
        }

        foreach (string segment in keyPath)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                throw new ArgumentException("Key-path segments cannot be empty.", nameof(keyPath));
            }
        }

        Name = name;
        KeyPath = keyPath;
        Unique = unique;
        MultiEntry = multiEntry;
    }

    /// <summary>Gets the index name, unique within its owning object store.</summary>
    public string Name { get; }

    /// <summary>Gets the property-path segments the index is built from.</summary>
    public IReadOnlyList<string> KeyPath { get; }

    /// <summary>Gets whether the browser rejects a second record with the same index key.</summary>
    public bool Unique { get; }

    /// <summary>Gets whether an array-valued key path indexes each element separately.</summary>
    public bool MultiEntry { get; }
}

/// <summary>
/// Declares one IndexedDB object store: its name, primary key shape, generated-key behavior, and
/// secondary indexes.
/// </summary>
/// <remarks>
/// This is a schema declaration only. It carries no persistence behavior; <c>P2-IDB-004</c>/
/// <c>P2-IDB-005</c> implement the JS transaction primitives and C# repositories that read it.
/// </remarks>
public sealed record IndexedDbObjectStoreDefinition
{
    /// <summary>Initializes a validated object store declaration.</summary>
    /// <param name="name">Non-empty, database-unique object store name.</param>
    /// <param name="keyPath">
    /// Property-path segments forming the in-line primary key, or an empty list for an
    /// out-of-line key supplied explicitly by the caller on every operation.
    /// </param>
    /// <param name="autoIncrement">Whether the browser generates the primary key.</param>
    /// <param name="indexes">Secondary indexes declared on this store; names must be unique.</param>
    /// <param name="purpose">Short human-readable statement of what the store persists and why.</param>
    /// <exception cref="ArgumentException">
    /// The name or purpose is empty, a key-path segment is empty, or two indexes share a name.
    /// </exception>
    public IndexedDbObjectStoreDefinition(
        string name,
        IReadOnlyList<string> keyPath,
        bool autoIncrement,
        IReadOnlyList<IndexedDbIndexDefinition> indexes,
        string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentNullException.ThrowIfNull(keyPath);
        ArgumentNullException.ThrowIfNull(indexes);

        foreach (string segment in keyPath)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                throw new ArgumentException("Key-path segments cannot be empty.", nameof(keyPath));
            }
        }

        if (autoIncrement && keyPath.Count > 1)
        {
            throw new ArgumentException(
                "An auto-incrementing store supports at most one key-path segment.", nameof(keyPath));
        }

        if (indexes.Select(index => index.Name).Distinct(StringComparer.Ordinal).Count() != indexes.Count)
        {
            throw new ArgumentException("Index names must be unique within one object store.", nameof(indexes));
        }

        Name = name;
        KeyPath = keyPath;
        AutoIncrement = autoIncrement;
        Indexes = indexes;
        Purpose = purpose;
    }

    /// <summary>Gets the database-unique object store name.</summary>
    public string Name { get; }

    /// <summary>
    /// Gets the in-line primary-key path segments, or an empty list when the key is supplied
    /// out-of-line by the caller on every operation.
    /// </summary>
    public IReadOnlyList<string> KeyPath { get; }

    /// <summary>Gets whether the browser generates the primary key.</summary>
    public bool AutoIncrement { get; }

    /// <summary>Gets the secondary indexes declared on this store.</summary>
    public IReadOnlyList<IndexedDbIndexDefinition> Indexes { get; }

    /// <summary>Gets a short human-readable statement of what the store persists and why.</summary>
    public string Purpose { get; }
}

/// <summary>
/// Names one set of object stores that a single IndexedDB transaction must cover atomically, and
/// explains why those stores (and no others) are grouped together.
/// </summary>
public sealed record IndexedDbTransactionBoundary
{
    /// <summary>Initializes a validated transaction boundary declaration.</summary>
    /// <param name="name">Non-empty boundary name used by repository code and tests.</param>
    /// <param name="objectStoreNames">One or more object store names committed together.</param>
    /// <param name="description">Explanation of what is atomic and why other stores are excluded.</param>
    /// <exception cref="ArgumentException">The name/description is empty or no store is named.</exception>
    public IndexedDbTransactionBoundary(
        string name,
        IReadOnlyList<string> objectStoreNames,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (objectStoreNames is not { Count: > 0 })
        {
            throw new ArgumentException("A transaction boundary requires at least one object store.", nameof(objectStoreNames));
        }

        Name = name;
        ObjectStoreNames = objectStoreNames;
        Description = description;
    }

    /// <summary>Gets the boundary name used by repository code and tests.</summary>
    public string Name { get; }

    /// <summary>Gets the object store names committed together in one transaction.</summary>
    public IReadOnlyList<string> ObjectStoreNames { get; }

    /// <summary>Gets the explanation of what is atomic and why other stores are excluded.</summary>
    public string Description { get; }
}
