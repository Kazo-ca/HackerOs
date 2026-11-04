using Kazo.Os.AppSdk.WebData;

namespace Kazo.Os.AppSdk.Web.Data;

/// <summary>
/// Declares the versions of a site database (ADR 0088 §2), as the <c>onupgradeneeded</c> handler of an
/// IndexedDB page: each <see cref="Version"/> says what changes to reach it. The database opens at the
/// highest version; a browser holding an older one runs the steps above it, in order.
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddKazoWebDb(db => db
///     .Version(1, v => v.CreateStore("notes", keyPath: "id", autoIncrement: true))
///     .Version(2, v => v.CreateIndex("notes", "byAuthor", "author")));
/// </code>
/// </example>
public sealed class KazoWebDbSchemaBuilder
{
    private readonly SortedDictionary<int, KazoWebDbVersionBuilder> _versions = [];

    /// <summary>Declares what version <paramref name="version"/> changes.</summary>
    /// <param name="version">1, then 2… with no gap.</param>
    /// <param name="upgrade">The changes.</param>
    /// <exception cref="ArgumentException">The version is not positive or is declared twice.</exception>
    public KazoWebDbSchemaBuilder Version(int version, Action<KazoWebDbVersionBuilder> upgrade)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentNullException.ThrowIfNull(upgrade);
        if (_versions.ContainsKey(version))
        {
            throw new ArgumentException($"Version {version} is declared twice.", nameof(version));
        }

        KazoWebDbVersionBuilder step = new();
        upgrade(step);
        _versions[version] = step;
        return this;
    }

    /// <summary>Builds the schema the gateway receives.</summary>
    /// <exception cref="InvalidOperationException">No version, or a gap between versions.</exception>
    internal KazoWebDbSchemaDefinition Build()
    {
        if (_versions.Count == 0)
        {
            throw new InvalidOperationException("A site database needs at least Version(1, ...).");
        }

        int expected = 1;
        foreach (int version in _versions.Keys)
        {
            if (version != expected++)
            {
                throw new InvalidOperationException($"Database versions must be 1, 2, 3... with no gap (version {expected - 1} is missing).");
            }
        }

        return new KazoWebDbSchemaDefinition(_versions.Keys.Last(), [.. _versions.Select(pair => pair.Value.Build(pair.Key))]);
    }
}

/// <summary>The changes of one version: stores and indexes to create or delete.</summary>
public sealed class KazoWebDbVersionBuilder
{
    private readonly List<KazoWebDbStoreDefinition> _createStores = [];
    private readonly List<string> _deleteStores = [];
    private readonly List<KazoWebDbIndexChange> _createIndexes = [];
    private readonly List<KazoWebDbIndexChange> _deleteIndexes = [];

    /// <summary>Creates an object store (<c>createObjectStore</c>).</summary>
    /// <param name="name">Store name.</param>
    /// <param name="keyPath">The property holding the key, in the JSON of the value (camelCase with the web defaults); <see langword="null"/> for keys given apart.</param>
    /// <param name="autoIncrement">Whether the store numbers new records 1, 2, 3…</param>
    public KazoWebDbVersionBuilder CreateStore(string name, string? keyPath = null, bool autoIncrement = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _createStores.Add(new KazoWebDbStoreDefinition(name, keyPath, autoIncrement, []));
        return this;
    }

    /// <summary>Deletes an object store and its records (<c>deleteObjectStore</c>).</summary>
    /// <param name="name">Store name.</param>
    public KazoWebDbVersionBuilder DeleteStore(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _deleteStores.Add(name);
        return this;
    }

    /// <summary>Adds an index to a store (<c>createIndex</c>), created in this version or before.</summary>
    /// <param name="store">Store name.</param>
    /// <param name="name">Index name.</param>
    /// <param name="keyPath">The property the index reads.</param>
    /// <param name="unique">Whether two records may not share the value (<c>ConstraintError</c>).</param>
    /// <param name="multiEntry">Whether an array adds one entry per element (tags).</param>
    public KazoWebDbVersionBuilder CreateIndex(string store, string name, string keyPath, bool unique = false, bool multiEntry = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        _createIndexes.Add(new KazoWebDbIndexChange(store, new KazoWebDbIndexDefinition(name, keyPath, unique, multiEntry)));
        return this;
    }

    /// <summary>Removes an index (<c>deleteIndex</c>).</summary>
    /// <param name="store">Store name.</param>
    /// <param name="name">Index name.</param>
    public KazoWebDbVersionBuilder DeleteIndex(string store, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _deleteIndexes.Add(new KazoWebDbIndexChange(store, new KazoWebDbIndexDefinition(name, name)));
        return this;
    }

    internal KazoWebDbMigrationStep Build(int version) =>
        new(version, [.. _createStores], [.. _deleteStores], [.. _createIndexes], [.. _deleteIndexes]);
}
