using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kazo.Os.AppSdk.WebData;

/// <summary>
/// An IndexedDB database kept in memory, with IndexedDB's rules (ADR 0088 §2): versions upgraded step by
/// step, key paths, key generators, unique and multi-entry indexes, key ordering
/// (numbers &lt; strings &lt; arrays) and all-or-nothing transactions. The OS uses it where the host has
/// no IndexedDB (headless hosts, tests); a site's tests use it through <see cref="KazoWebDbInMemoryBackend"/>.
/// </summary>
/// <remarks>Thread-safe: every call runs under one lock, as IndexedDB serializes overlapping read-write transactions.</remarks>
public sealed class InMemoryWebDatabase
{
    private readonly Lock _gate = new();
    private Dictionary<string, StoreState> _stores = new(StringComparer.Ordinal);

    /// <summary>Initializes an empty database (version 0, no stores).</summary>
    /// <param name="name">The database name.</param>
    public InMemoryWebDatabase(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Gets the database name.</summary>
    public string Name { get; }

    /// <summary>Gets the stored version (0 before the first open).</summary>
    public int Version
    {
        get
        {
            lock (_gate)
            {
                return field;
            }
        }

        private set;
    }

    /// <summary>Opens the database at the schema's version, applying the steps above the stored version.</summary>
    /// <param name="schema">The schema.</param>
    /// <exception cref="KazoWebDbException"><c>VersionError</c> for an older schema; <c>InvalidStateError</c> for a step that cannot apply.</exception>
    public void Open(KazoWebDbSchemaDefinition schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        lock (_gate)
        {
            int current = Version;
            if (schema.Version < 1)
            {
                throw new KazoWebDbException(KazoWebDbException.DataError, "The version must be a positive integer.");
            }

            if (schema.Version < current)
            {
                throw new KazoWebDbException(KazoWebDbException.VersionError,
                    $"The requested version ({schema.Version}) is less than the existing version ({current}).");
            }

            if (schema.Version == current)
            {
                return;
            }

            // onupgradeneeded runs in a versionchange transaction: a failing step leaves the old version.
            Dictionary<string, StoreState> upgraded = _stores.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
            for (int version = current + 1; version <= schema.Version; version++)
            {
                KazoWebDbMigrationStep step = schema.Steps.SingleOrDefault(candidate => candidate.TargetVersion == version)
                    ?? throw new KazoWebDbException(KazoWebDbException.InvalidStateError, $"No migration step reaches version {version}.");
                Apply(upgraded, step);
            }

            _stores = upgraded;
            Version = schema.Version;
        }
    }

    /// <summary>Runs a batch in one transaction; on any failure nothing of it is kept.</summary>
    /// <param name="request">The batch.</param>
    /// <returns>One JSON result per operation (see <see cref="IAppWebDatabaseGateway.ExecuteAsync"/>).</returns>
    /// <exception cref="KazoWebDbException">An operation failed.</exception>
    public IReadOnlyList<string?> Execute(KazoWebDbRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (Version == 0 || request.Version != Version)
            {
                throw new KazoWebDbException(KazoWebDbException.InvalidStateError,
                    $"The database '{Name}' is not open at version {request.Version}.");
            }

            if (request.Stores.Count == 0)
            {
                throw new KazoWebDbException(KazoWebDbException.InvalidStateError, "A transaction needs at least one object store.");
            }

            Dictionary<string, StoreState> scope = new(StringComparer.Ordinal);
            foreach (string name in request.Stores)
            {
                if (!_stores.TryGetValue(name, out StoreState? store))
                {
                    throw new KazoWebDbException(KazoWebDbException.NotFoundError, $"The object store '{name}' does not exist.");
                }

                // Copy-on-write: a failed transaction drops the copies.
                scope[name] = request.ReadWrite ? store.Clone() : store;
            }

            List<string?> results = new(request.Operations.Count);
            foreach (KazoWebDbOperation operation in request.Operations)
            {
                if (!scope.TryGetValue(operation.Store, out StoreState? store))
                {
                    throw new KazoWebDbException(KazoWebDbException.NotFoundError,
                        $"The object store '{operation.Store}' is not in the transaction's scope.");
                }

                if (KazoWebDbOperationKinds.IsWrite(operation.Kind) && !request.ReadWrite)
                {
                    throw new KazoWebDbException("ReadOnlyError", "The transaction is read-only.");
                }

                results.Add(Run(store, operation));
            }

            if (request.ReadWrite)
            {
                foreach ((string name, StoreState store) in scope)
                {
                    _stores[name] = store;
                }
            }

            return results;
        }
    }

    /// <summary>Describes the stores: name and record count, and the size of the records' JSON in UTF-8 bytes.</summary>
    /// <returns>The stores in name order and the total size.</returns>
    public (IReadOnlyList<(string Name, int Records)> Stores, long SizeBytes) Describe()
    {
        lock (_gate)
        {
            long size = 0;
            List<(string, int)> stores = [];
            foreach ((string name, StoreState store) in _stores.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                stores.Add((name, store.Records.Count));
                foreach ((WebDbKey key, string value) in store.Records)
                {
                    size += Encoding.UTF8.GetByteCount(value) + Encoding.UTF8.GetByteCount(key.Json);
                }
            }

            return (stores, size);
        }
    }

    private static void Apply(Dictionary<string, StoreState> stores, KazoWebDbMigrationStep step)
    {
        foreach (string name in step.DeleteStores ?? [])
        {
            if (!stores.Remove(name))
            {
                throw new KazoWebDbException(KazoWebDbException.NotFoundError, $"The object store '{name}' does not exist.");
            }
        }

        foreach (KazoWebDbStoreDefinition definition in step.CreateStores ?? [])
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
            if (stores.ContainsKey(definition.Name))
            {
                throw new KazoWebDbException(KazoWebDbException.ConstraintError, $"The object store '{definition.Name}' already exists.");
            }

            if (definition.AutoIncrement && definition.KeyPath is { Length: 0 })
            {
                throw new KazoWebDbException("InvalidAccessError", "An auto-increment store cannot have an empty key path.");
            }

            StoreState store = new(definition.KeyPath, definition.AutoIncrement);
            foreach (KazoWebDbIndexDefinition index in definition.Indexes ?? [])
            {
                store.AddIndex(index);
            }

            stores[definition.Name] = store;
        }

        foreach (KazoWebDbIndexChange change in step.DeleteIndexes ?? [])
        {
            if (!stores.TryGetValue(change.Store, out StoreState? store) || !store.Indexes.Remove(change.Index.Name))
            {
                throw new KazoWebDbException(KazoWebDbException.NotFoundError, $"The index '{change.Index.Name}' of '{change.Store}' does not exist.");
            }
        }

        foreach (KazoWebDbIndexChange change in step.CreateIndexes ?? [])
        {
            if (!stores.TryGetValue(change.Store, out StoreState? store))
            {
                throw new KazoWebDbException(KazoWebDbException.NotFoundError, $"The object store '{change.Store}' does not exist.");
            }

            store.AddIndex(change.Index);
        }
    }

    private static string? Run(StoreState store, KazoWebDbOperation operation)
    {
        WebDbRange? range = operation.Range is { } r ? WebDbRange.From(r) : null;
        WebDbKey? key = operation.Key is { } k ? WebDbKey.Parse(k) : null;
        if (key is not null && range is null && operation.Kind is not (KazoWebDbOperationKinds.Add or KazoWebDbOperationKinds.Put))
        {
            range = WebDbRange.Only(key);
        }

        KazoWebDbIndexDefinition? index = null;
        if (operation.Index is { } indexName && !store.Indexes.TryGetValue(indexName, out index))
        {
            throw new KazoWebDbException(KazoWebDbException.NotFoundError, $"The index '{indexName}' does not exist.");
        }

        switch (operation.Kind)
        {
            case KazoWebDbOperationKinds.Get:
                if (range is null)
                {
                    throw new KazoWebDbException(KazoWebDbException.DataError, "get needs a key or a key range.");
                }

                return Entries(store, index, range).Select(entry => entry.Value).FirstOrDefault();
            case KazoWebDbOperationKinds.GetAll:
                return JsonArray(Entries(store, index, range).Take(Limit(operation.Count)).Select(entry => entry.Value));
            case KazoWebDbOperationKinds.GetAllKeys:
                return JsonArray(Entries(store, index, range).Take(Limit(operation.Count)).Select(entry => entry.Primary.Json));
            case KazoWebDbOperationKinds.Count:
                return Entries(store, index, range).Count().ToString(CultureInfo.InvariantCulture);
            case KazoWebDbOperationKinds.Add:
            case KazoWebDbOperationKinds.Put:
                if (index is not null)
                {
                    throw new KazoWebDbException(KazoWebDbException.InvalidStateError, $"{operation.Kind} is not an index method.");
                }

                return store.Store(operation.Value ?? throw new KazoWebDbException(KazoWebDbException.DataError, "No value to store."),
                    key, overwrite: operation.Kind == KazoWebDbOperationKinds.Put).Json;
            case KazoWebDbOperationKinds.Delete:
                if (range is null)
                {
                    throw new KazoWebDbException(KazoWebDbException.DataError, "delete needs a key or a key range.");
                }

                foreach (WebDbKey deleted in store.Records.Keys.Where(range.Contains).ToList())
                {
                    store.Records.Remove(deleted);
                }

                return null;
            case KazoWebDbOperationKinds.Clear:
                store.Records.Clear();
                return null;
            default:
                throw new KazoWebDbException(KazoWebDbException.InvalidStateError, $"Unknown operation '{operation.Kind}'.");
        }
    }

    private static int Limit(int? count) => count is > 0 ? count.Value : int.MaxValue;

    private static string JsonArray(IEnumerable<string> items) => "[" + string.Join(",", items) + "]";

    /// <summary>Records in the order a cursor would visit them: by primary key, or by index key then primary key.</summary>
    private static IEnumerable<(WebDbKey Primary, string Value)> Entries(StoreState store, KazoWebDbIndexDefinition? index, WebDbRange? range)
    {
        if (index is null)
        {
            return store.Records.Where(pair => range is null || range.Contains(pair.Key)).Select(pair => (pair.Key, pair.Value));
        }

        List<(WebDbKey IndexKey, WebDbKey Primary, string Value)> entries = [];
        foreach ((WebDbKey primary, string value) in store.Records)
        {
            foreach (WebDbKey indexKey in StoreState.IndexKeys(index, value))
            {
                if (range is null || range.Contains(indexKey))
                {
                    entries.Add((indexKey, primary, value));
                }
            }
        }

        return entries
            .OrderBy(entry => entry.IndexKey, WebDbKey.Comparer)
            .ThenBy(entry => entry.Primary, WebDbKey.Comparer)
            .Select(entry => (entry.Primary, entry.Value));
    }

    /// <summary>One object store: its records sorted by key, its key generator and its indexes.</summary>
    private sealed class StoreState(string? keyPath, bool autoIncrement)
    {
        public string? KeyPath { get; } = keyPath;

        public bool AutoIncrement { get; } = autoIncrement;

        public long NextKey { get; set; } = 1;

        public SortedDictionary<WebDbKey, string> Records { get; private init; } = new(WebDbKey.Comparer);

        public Dictionary<string, KazoWebDbIndexDefinition> Indexes { get; private init; } = new(StringComparer.Ordinal);

        public StoreState Clone() => new(KeyPath, AutoIncrement)
        {
            NextKey = NextKey,
            Records = new SortedDictionary<WebDbKey, string>(Records, WebDbKey.Comparer),
            Indexes = new Dictionary<string, KazoWebDbIndexDefinition>(Indexes, StringComparer.Ordinal),
        };

        public void AddIndex(KazoWebDbIndexDefinition index)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(index.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(index.KeyPath);
            if (Indexes.ContainsKey(index.Name))
            {
                throw new KazoWebDbException(KazoWebDbException.ConstraintError, $"The index '{index.Name}' already exists.");
            }

            // createIndex on a store holding records that break a unique index aborts the upgrade.
            if (index.Unique)
            {
                CheckUnique(index, Records.Select(pair => (pair.Key, pair.Value)));
            }

            Indexes[index.Name] = index;
        }

        /// <summary>The keys a record adds to an index: none when the path has no valid key.</summary>
        public static IEnumerable<WebDbKey> IndexKeys(KazoWebDbIndexDefinition index, string value)
        {
            using JsonDocument document = JsonDocument.Parse(value);
            if (!TryEvaluate(document.RootElement, index.KeyPath, out JsonElement found))
            {
                return [];
            }

            if (index.MultiEntry && found.ValueKind == JsonValueKind.Array)
            {
                // Invalid elements are skipped, duplicates added once.
                List<WebDbKey> keys = [];
                foreach (JsonElement item in found.EnumerateArray())
                {
                    if (WebDbKey.TryFrom(item, out WebDbKey? key) && !keys.Any(existing => WebDbKey.Comparer.Compare(existing, key) == 0))
                    {
                        keys.Add(key!);
                    }
                }

                return keys;
            }

            return WebDbKey.TryFrom(found, out WebDbKey? single) ? [single!] : [];
        }

        /// <summary>Stores a record (<c>add</c> or <c>put</c>) and returns its key.</summary>
        public WebDbKey Store(string json, WebDbKey? explicitKey, bool overwrite)
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(json);
            }
            catch (JsonException exception)
            {
                throw new KazoWebDbException(KazoWebDbException.DataError, $"The value is not JSON: {exception.Message}");
            }

            WebDbKey key;
            if (KeyPath is not null)
            {
                if (explicitKey is not null)
                {
                    throw new KazoWebDbException(KazoWebDbException.DataError, "The store uses in-line keys and the key parameter was provided.");
                }

                using JsonDocument document = JsonDocument.Parse(json);
                if (TryEvaluate(document.RootElement, KeyPath, out JsonElement found))
                {
                    key = WebDbKey.TryFrom(found, out WebDbKey? inline) ? inline!
                        : throw new KazoWebDbException(KazoWebDbException.DataError, $"The value at key path '{KeyPath}' is not a valid key.");
                }
                else if (AutoIncrement)
                {
                    key = WebDbKey.FromNumber(NextKey);
                    node = Inject(node, KeyPath, NextKey);
                    json = node!.ToJsonString();
                }
                else
                {
                    throw new KazoWebDbException(KazoWebDbException.DataError, $"The value has no key at key path '{KeyPath}'.");
                }
            }
            else if (explicitKey is not null)
            {
                key = explicitKey;
            }
            else if (AutoIncrement)
            {
                key = WebDbKey.FromNumber(NextKey);
            }
            else
            {
                throw new KazoWebDbException(KazoWebDbException.DataError, "The store uses out-of-line keys and has no key generator, and no key was provided.");
            }

            if (!overwrite && Records.ContainsKey(key))
            {
                throw new KazoWebDbException(KazoWebDbException.ConstraintError, "Key already exists in the object store.");
            }

            foreach (KazoWebDbIndexDefinition index in Indexes.Values.Where(index => index.Unique))
            {
                CheckUnique(index, Records.Where(pair => WebDbKey.Comparer.Compare(pair.Key, key) != 0)
                    .Select(pair => (pair.Key, pair.Value)).Append((key, json)));
            }

            // The key generator moves past any numeric key stored, explicit or not.
            if (AutoIncrement && key.Kind == WebDbKeyKind.Number && key.Number >= NextKey)
            {
                NextKey = (long)Math.Floor(key.Number) + 1;
            }

            Records[key] = json;
            return key;
        }

        private static void CheckUnique(KazoWebDbIndexDefinition index, IEnumerable<(WebDbKey Primary, string Value)> records)
        {
            List<WebDbKey> seen = [];
            foreach ((WebDbKey _, string value) in records)
            {
                foreach (WebDbKey indexKey in IndexKeys(index, value))
                {
                    if (seen.Any(existing => WebDbKey.Comparer.Compare(existing, indexKey) == 0))
                    {
                        throw new KazoWebDbException(KazoWebDbException.ConstraintError,
                            $"Unable to add key to index '{index.Name}': at least one key does not satisfy the uniqueness requirements.");
                    }

                    seen.Add(indexKey);
                }
            }
        }

        private static JsonNode? Inject(JsonNode? node, string keyPath, long key)
        {
            if (node is not JsonObject root)
            {
                throw new KazoWebDbException(KazoWebDbException.DataError, "A generated key can only be injected into an object.");
            }

            string[] parts = keyPath.Split('.');
            JsonObject current = root;
            foreach (string part in parts[..^1])
            {
                if (current[part] is JsonObject child)
                {
                    current = child;
                }
                else if (current[part] is null)
                {
                    JsonObject created = new();
                    current[part] = created;
                    current = created;
                }
                else
                {
                    throw new KazoWebDbException(KazoWebDbException.DataError, $"The key path '{keyPath}' cannot be set on this value.");
                }
            }

            current[parts[^1]] = key;
            return root;
        }
    }

    /// <summary>Evaluates a dotted key path, as IndexedDB's "evaluate a key path on a value".</summary>
    internal static bool TryEvaluate(JsonElement value, string keyPath, out JsonElement found)
    {
        found = value;
        if (keyPath.Length == 0)
        {
            return true;
        }

        foreach (string part in keyPath.Split('.'))
        {
            if (found.ValueKind != JsonValueKind.Object || !found.TryGetProperty(part, out JsonElement next))
            {
                return false;
            }

            found = next;
        }

        return true;
    }
}

/// <summary>Kinds of IndexedDB keys this database stores, in their sort order.</summary>
internal enum WebDbKeyKind
{
    Number = 0,
    String = 1,
    Array = 2,
}

/// <summary>A valid IndexedDB key: a number, a string or an array of keys; compared as IndexedDB compares keys.</summary>
internal sealed class WebDbKey
{
    private WebDbKey(WebDbKeyKind kind, double number, string? text, IReadOnlyList<WebDbKey>? items, string json)
    {
        Kind = kind;
        Number = number;
        Text = text;
        Items = items;
        Json = json;
    }

    public static IComparer<WebDbKey> Comparer { get; } = Comparer<WebDbKey>.Create(Compare);

    public WebDbKeyKind Kind { get; }

    public double Number { get; }

    public string? Text { get; }

    public IReadOnlyList<WebDbKey>? Items { get; }

    /// <summary>Gets the key as JSON, as the page's script would see it.</summary>
    public string Json { get; }

    public static WebDbKey FromNumber(double number) =>
        new(WebDbKeyKind.Number, number, null, null, JsonSerializer.Serialize(number, KazoWebDbKeyJsonContext.Default.Double));

    public static WebDbKey Parse(string json)
    {
        using JsonDocument document = ParseDocument(json);
        return TryFrom(document.RootElement, out WebDbKey? key) ? key!
            : throw new KazoWebDbException(KazoWebDbException.DataError, $"'{json}' is not a valid key.");
    }

    public static bool TryFrom(JsonElement element, out WebDbKey? key)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetDouble(out double number) && !double.IsNaN(number):
                key = FromNumber(number);
                return true;
            case JsonValueKind.String:
                string text = element.GetString()!;
                key = new WebDbKey(WebDbKeyKind.String, 0, text, null, JsonSerializer.Serialize(text, KazoWebDbKeyJsonContext.Default.String));
                return true;
            case JsonValueKind.Array:
                List<WebDbKey> items = [];
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (!TryFrom(item, out WebDbKey? child))
                    {
                        key = null;
                        return false;
                    }

                    items.Add(child!);
                }

                key = new WebDbKey(WebDbKeyKind.Array, 0, null, items, "[" + string.Join(",", items.Select(item => item.Json)) + "]");
                return true;
            default:
                key = null;
                return false;
        }
    }

    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new KazoWebDbException(KazoWebDbException.DataError, $"'{json}' is not a valid key.");
        }
    }

    private static int Compare(WebDbKey? left, WebDbKey? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null || right is null)
        {
            return left is null ? -1 : 1;
        }

        if (left.Kind != right.Kind)
        {
            return left.Kind.CompareTo(right.Kind);
        }

        switch (left.Kind)
        {
            case WebDbKeyKind.Number:
                return left.Number.CompareTo(right.Number);
            case WebDbKeyKind.String:
                // IndexedDB compares strings by UTF-16 code units.
                return string.CompareOrdinal(left.Text, right.Text);
            default:
                for (int i = 0; i < Math.Min(left.Items!.Count, right.Items!.Count); i++)
                {
                    int compared = Compare(left.Items[i], right.Items[i]);
                    if (compared != 0)
                    {
                        return compared;
                    }
                }

                return left.Items.Count.CompareTo(right.Items.Count);
        }
    }
}

/// <summary>An <c>IDBKeyRange</c> with parsed bounds.</summary>
internal sealed class WebDbRange(WebDbKey? lower, WebDbKey? upper, bool lowerOpen, bool upperOpen)
{
    public static WebDbRange Only(WebDbKey key) => new(key, key, false, false);

    public static WebDbRange From(KazoWebDbRange range)
    {
        WebDbKey? lower = range.Lower is { } l ? WebDbKey.Parse(l) : null;
        WebDbKey? upper = range.Upper is { } u ? WebDbKey.Parse(u) : null;
        if (lower is null && upper is null)
        {
            throw new KazoWebDbException(KazoWebDbException.DataError, "A key range needs at least one bound.");
        }

        if (lower is not null && upper is not null)
        {
            int compared = WebDbKey.Comparer.Compare(lower, upper);
            if (compared > 0 || (compared == 0 && (range.LowerOpen || range.UpperOpen)))
            {
                throw new KazoWebDbException(KazoWebDbException.DataError, "The lower bound is greater than the upper bound.");
            }
        }

        return new WebDbRange(lower, upper, range.LowerOpen, range.UpperOpen);
    }

    public bool Contains(WebDbKey key)
    {
        if (lower is not null)
        {
            int compared = WebDbKey.Comparer.Compare(key, lower);
            if (compared < 0 || (compared == 0 && lowerOpen))
            {
                return false;
            }
        }

        if (upper is not null)
        {
            int compared = WebDbKey.Comparer.Compare(key, upper);
            if (compared > 0 || (compared == 0 && upperOpen))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>JSON of keys (numbers written as JavaScript writes them).</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(double))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class KazoWebDbKeyJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

/// <summary>
/// A site database kept in memory, as a stand-in for <see cref="IAppWebDatabaseGateway"/>: a site's tests
/// register it in the site's services (<c>builder.Services.AddSingleton&lt;IAppWebDatabaseGateway&gt;(new KazoWebDbInMemoryBackend("kazoweb.test"))</c>)
/// so <c>KazoWebDb</c> runs without the OS.
/// </summary>
public sealed class KazoWebDbInMemoryBackend : IAppWebDatabaseGateway
{
    /// <summary>Initializes an empty database.</summary>
    /// <param name="databaseName">The database name, <c>kazoweb.&lt;app id&gt;</c>.</param>
    public KazoWebDbInMemoryBackend(string databaseName)
    {
        Database = new InMemoryWebDatabase(databaseName);
    }

    /// <summary>Gets the database, to inspect it from a test.</summary>
    public InMemoryWebDatabase Database { get; }

    /// <inheritdoc />
    public string DatabaseName => Database.Name;

    /// <inheritdoc />
    public ValueTask OpenAsync(KazoWebDbSchemaDefinition schema, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Database.Open(schema);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string?>> ExecuteAsync(KazoWebDbRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Database.Execute(request));
    }
}
