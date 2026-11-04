using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Kazo.Os.AppSdk.Web.Hosting;
using Kazo.Os.AppSdk.WebData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kazo.Os.AppSdk.Web.Data;

/// <summary>
/// The database of a site (ADR 0073 §3, ADR 0088): the IndexedDB database <c>hackweb.&lt;app id&gt;</c> of the
/// host browser, which the OS opens for the site. Its data never lands in the OS files, survives the
/// site's restarts and is deleted when the site is uninstalled. Registered by
/// <see cref="KazoWebDbServiceCollectionExtensions.AddHackWebDb"/>; a singleton of the site.
/// </summary>
/// <remarks>
/// <para>
/// Values are stored as JSON with the site's options (<see cref="KazoWebApplicationBuilder.Json"/>):
/// add the site's source-generated context there for a trimmed publish. Key paths name JSON properties,
/// so camelCase with the web defaults (<c>keyPath: "id"</c> for a property <c>Id</c>).
/// </para>
/// <para>
/// Each read is its own read-only transaction. Several writes that must succeed together go in a
/// <see cref="KazoWebDbTransaction"/> (<see cref="BeginTransaction"/>), sent as one batch.
/// </para>
/// </remarks>
public sealed class KazoWebDb
{
    private readonly IAppWebDatabaseGateway _gateway;
    private readonly KazoWebDbSchemaDefinition _schema;
    private readonly JsonSerializerOptions _json;
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private volatile bool _opened;

    /// <summary>Initializes the database of a site.</summary>
    /// <param name="gateway">The OS gateway (or <see cref="KazoWebDbInMemoryBackend"/> in a test).</param>
    /// <param name="schema">The versions the site declares.</param>
    /// <param name="json">The site's JSON options.</param>
    public KazoWebDb(IAppWebDatabaseGateway gateway, KazoWebDbSchemaBuilder schema, KazoWebJsonOptions json)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(json);
        _gateway = gateway;
        _schema = schema.Build();
        _json = json.SerializerOptions;
    }

    /// <summary>Gets the database name, <c>hackweb.&lt;app id&gt;</c>.</summary>
    public string Name => _gateway.DatabaseName;

    /// <summary>Gets the version the site declares (the highest of its schema).</summary>
    public int Version => _schema.Version;

    /// <summary>Gets an object store whose values are <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The value type, read and written as JSON.</typeparam>
    /// <param name="name">Store name.</param>
    public KazoWebDbStore<T> Store<T>(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new KazoWebDbStore<T>(this, name);
    }

    /// <summary>Starts a read-write transaction over <paramref name="stores"/>: queue writes, then <see cref="KazoWebDbTransaction.CommitAsync"/>.</summary>
    /// <param name="stores">The stores the transaction writes to.</param>
    public KazoWebDbTransaction BeginTransaction(params string[] stores)
    {
        ArgumentNullException.ThrowIfNull(stores);
        if (stores.Length == 0)
        {
            throw new ArgumentException("A transaction needs at least one object store.", nameof(stores));
        }

        return new KazoWebDbTransaction(this, stores);
    }

    /// <summary>Opens the database at the site's version; the first call does it, the next ones return at once.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (_opened)
        {
            return;
        }

        await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_opened)
            {
                await _gateway.OpenAsync(_schema, cancellationToken).ConfigureAwait(false);
                _opened = true;
            }
        }
        finally
        {
            _openGate.Release();
        }
    }

    internal async Task<IReadOnlyList<string?>> RunAsync(IReadOnlyList<string> stores, bool readWrite, IReadOnlyList<KazoWebDbOperation> operations, CancellationToken cancellationToken)
    {
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _gateway.ExecuteAsync(new KazoWebDbRequest(_schema.Version, stores, readWrite, operations), cancellationToken).ConfigureAwait(false);
    }

    internal async Task<string?> RunOneAsync(string store, bool readWrite, KazoWebDbOperation operation, CancellationToken cancellationToken) =>
        (await RunAsync([store], readWrite, [operation], cancellationToken).ConfigureAwait(false))[0];

    internal string Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Serialize(value, TypeInfo<T>());
    }

    internal T? Deserialize<T>(string json) => JsonSerializer.Deserialize(json, TypeInfo<T>());

    internal IReadOnlyList<T> DeserializeArray<T>(string? json)
    {
        if (json is null)
        {
            return [];
        }

        JsonTypeInfo<T> typeInfo = TypeInfo<T>();
        using JsonDocument document = JsonDocument.Parse(json);
        List<T> values = new(document.RootElement.GetArrayLength());
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            values.Add(item.Deserialize(typeInfo)!);
        }

        return values;
    }

    internal static IReadOnlyList<KazoWebDbKey> DeserializeKeys(string? json)
    {
        if (json is null)
        {
            return [];
        }

        using JsonDocument document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(item => KazoWebDbKey.FromJson(item.GetRawText()))];
    }

    /// <summary>
    /// The site's JSON metadata for <typeparamref name="T"/> (its <c>JsonSerializerContext</c>, trim-safe);
    /// plain values (<see cref="string"/>, numbers, <see cref="bool"/>) work even when the site's context
    /// does not list them, as a key-value store often holds them.
    /// </summary>
    private JsonTypeInfo<T> TypeInfo<T>()
    {
        try
        {
            return (JsonTypeInfo<T>)_json.GetTypeInfo(typeof(T));
        }
        catch (NotSupportedException) when (KazoWebDbSdkJsonContext.Default.GetTypeInfo(typeof(T)) is JsonTypeInfo<T> primitive)
        {
            return primitive;
        }
    }
}

/// <summary>An object store of a site database; values are <typeparamref name="T"/>.</summary>
/// <typeparam name="T">The value type.</typeparam>
public sealed class KazoWebDbStore<T>
{
    private readonly KazoWebDb _database;

    internal KazoWebDbStore(KazoWebDb database, string name)
    {
        _database = database;
        Name = name;
    }

    /// <summary>Gets the store name.</summary>
    public string Name { get; }

    /// <summary>Gets an index of the store.</summary>
    /// <param name="name">Index name.</param>
    public KazoWebDbIndex<T> Index(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new KazoWebDbIndex<T>(_database, Name, name);
    }

    /// <summary>Returns the value of <paramref name="key"/>, or <see langword="default"/>.</summary>
    public async Task<T?> GetAsync(KazoWebDbKey key, CancellationToken cancellationToken = default) =>
        await _database.RunOneAsync(Name, false, new KazoWebDbOperation(KazoWebDbOperationKinds.Get, Name, Key: key.Json), cancellationToken).ConfigureAwait(false)
            is { } json ? _database.Deserialize<T>(json) : default;

    /// <summary>Returns the values in key order, within <paramref name="range"/> and up to <paramref name="count"/>.</summary>
    public async Task<IReadOnlyList<T>> GetAllAsync(KazoWebDbKeyRange? range = null, int? count = null, CancellationToken cancellationToken = default) =>
        _database.DeserializeArray<T>(await _database.RunOneAsync(Name, false,
            new KazoWebDbOperation(KazoWebDbOperationKinds.GetAll, Name, Range: range?.Range, Count: count), cancellationToken).ConfigureAwait(false));

    /// <summary>Returns the keys in order, within <paramref name="range"/> and up to <paramref name="count"/>.</summary>
    public async Task<IReadOnlyList<KazoWebDbKey>> GetAllKeysAsync(KazoWebDbKeyRange? range = null, int? count = null, CancellationToken cancellationToken = default) =>
        KazoWebDb.DeserializeKeys(await _database.RunOneAsync(Name, false,
            new KazoWebDbOperation(KazoWebDbOperationKinds.GetAllKeys, Name, Range: range?.Range, Count: count), cancellationToken).ConfigureAwait(false));

    /// <summary>Counts the records, within <paramref name="range"/> when given.</summary>
    public async Task<int> CountAsync(KazoWebDbKeyRange? range = null, CancellationToken cancellationToken = default) =>
        int.Parse(await _database.RunOneAsync(Name, false,
            new KazoWebDbOperation(KazoWebDbOperationKinds.Count, Name, Range: range?.Range), cancellationToken).ConfigureAwait(false) ?? "0",
            System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Stores a new record and returns its key; <c>ConstraintError</c> when the key exists.</summary>
    /// <param name="value">The value.</param>
    /// <param name="key">The key, for a store without key path.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="KazoWebDbException">The write failed (<see cref="KazoWebDbException.Name"/> says why).</exception>
    public async Task<KazoWebDbKey> AddAsync(T value, KazoWebDbKey? key = null, CancellationToken cancellationToken = default) =>
        KazoWebDbKey.FromJson((await _database.RunOneAsync(Name, true, WriteOperation(_database, KazoWebDbOperationKinds.Add, Name, value, key), cancellationToken).ConfigureAwait(false))!);

    /// <summary>Stores a record, replacing the one of the same key, and returns its key.</summary>
    /// <param name="value">The value.</param>
    /// <param name="key">The key, for a store without key path.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<KazoWebDbKey> PutAsync(T value, KazoWebDbKey? key = null, CancellationToken cancellationToken = default) =>
        KazoWebDbKey.FromJson((await _database.RunOneAsync(Name, true, WriteOperation(_database, KazoWebDbOperationKinds.Put, Name, value, key), cancellationToken).ConfigureAwait(false))!);

    /// <summary>Removes the record of <paramref name="key"/> (nothing happens when there is none).</summary>
    public Task DeleteAsync(KazoWebDbKey key, CancellationToken cancellationToken = default) =>
        _database.RunOneAsync(Name, true, new KazoWebDbOperation(KazoWebDbOperationKinds.Delete, Name, Key: key.Json), cancellationToken);

    /// <summary>Removes the records within <paramref name="range"/>.</summary>
    public Task DeleteAsync(KazoWebDbKeyRange range, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(range);
        return _database.RunOneAsync(Name, true, new KazoWebDbOperation(KazoWebDbOperationKinds.Delete, Name, Range: range.Range), cancellationToken);
    }

    /// <summary>Removes every record.</summary>
    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        _database.RunOneAsync(Name, true, new KazoWebDbOperation(KazoWebDbOperationKinds.Clear, Name), cancellationToken);

    internal static KazoWebDbOperation WriteOperation(KazoWebDb database, string kind, string store, T value, KazoWebDbKey? key) =>
        new(kind, store, Key: key?.Json, Value: database.Serialize(value));
}

/// <summary>An index of a store: reads by the indexed property, in index key order.</summary>
/// <typeparam name="T">The value type of the store.</typeparam>
public sealed class KazoWebDbIndex<T>
{
    private readonly KazoWebDb _database;
    private readonly string _store;

    internal KazoWebDbIndex(KazoWebDb database, string store, string name)
    {
        _database = database;
        _store = store;
        Name = name;
    }

    /// <summary>Gets the index name.</summary>
    public string Name { get; }

    /// <summary>Returns the first value whose index key is <paramref name="key"/>, or <see langword="default"/>.</summary>
    public async Task<T?> GetAsync(KazoWebDbKey key, CancellationToken cancellationToken = default) =>
        await _database.RunOneAsync(_store, false, new KazoWebDbOperation(KazoWebDbOperationKinds.Get, _store, Name, Key: key.Json), cancellationToken).ConfigureAwait(false)
            is { } json ? _database.Deserialize<T>(json) : default;

    /// <summary>Returns the values whose index key is <paramref name="key"/>.</summary>
    public Task<IReadOnlyList<T>> GetAllAsync(KazoWebDbKey key, int? count = null, CancellationToken cancellationToken = default) =>
        GetAllAsync(KazoWebDbKeyRange.Only(key), count, cancellationToken);

    /// <summary>Returns the values in index key order, within <paramref name="range"/>.</summary>
    public async Task<IReadOnlyList<T>> GetAllAsync(KazoWebDbKeyRange? range = null, int? count = null, CancellationToken cancellationToken = default) =>
        _database.DeserializeArray<T>(await _database.RunOneAsync(_store, false,
            new KazoWebDbOperation(KazoWebDbOperationKinds.GetAll, _store, Name, Range: range?.Range, Count: count), cancellationToken).ConfigureAwait(false));

    /// <summary>Returns the primary keys in index key order, within <paramref name="range"/>.</summary>
    public async Task<IReadOnlyList<KazoWebDbKey>> GetAllKeysAsync(KazoWebDbKeyRange? range = null, int? count = null, CancellationToken cancellationToken = default) =>
        KazoWebDb.DeserializeKeys(await _database.RunOneAsync(_store, false,
            new KazoWebDbOperation(KazoWebDbOperationKinds.GetAllKeys, _store, Name, Range: range?.Range, Count: count), cancellationToken).ConfigureAwait(false));

    /// <summary>Counts the index entries, within <paramref name="range"/> when given.</summary>
    public async Task<int> CountAsync(KazoWebDbKeyRange? range = null, CancellationToken cancellationToken = default) =>
        int.Parse(await _database.RunOneAsync(_store, false,
            new KazoWebDbOperation(KazoWebDbOperationKinds.Count, _store, Name, Range: range?.Range), cancellationToken).ConfigureAwait(false) ?? "0",
            System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Writes that succeed or fail together (ADR 0088 §2): queued on <see cref="Store{T}"/>, sent as one batch
/// by <see cref="CommitAsync"/>, run in one <c>readwrite</c> IndexedDB transaction. Nothing is written
/// before the commit; a failed operation cancels them all.
/// </summary>
public sealed class KazoWebDbTransaction
{
    private readonly KazoWebDb _database;
    private readonly string[] _stores;
    private readonly List<KazoWebDbOperation> _operations = [];
    private bool _committed;

    internal KazoWebDbTransaction(KazoWebDb database, string[] stores)
    {
        _database = database;
        _stores = [.. stores];
    }

    /// <summary>Gets the number of queued writes.</summary>
    public int Count => _operations.Count;

    /// <summary>Gets a store of the transaction's scope, to queue writes on it.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="name">A store named in <see cref="KazoWebDb.BeginTransaction"/>.</param>
    public KazoWebDbTransactionStore<T> Store<T>(string name)
    {
        if (!_stores.Contains(name, StringComparer.Ordinal))
        {
            throw new ArgumentException($"The object store '{name}' is not in the transaction's scope.", nameof(name));
        }

        return new KazoWebDbTransactionStore<T>(this, name);
    }

    /// <summary>Sends the writes; returns the key of each <c>add</c>/<c>put</c> (and <see langword="null"/> for the others), in order.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="KazoWebDbException">An operation failed: nothing was written.</exception>
    /// <exception cref="InvalidOperationException">Already committed.</exception>
    public async Task<IReadOnlyList<KazoWebDbKey?>> CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_committed)
        {
            throw new InvalidOperationException("The transaction is already committed.");
        }

        _committed = true;
        if (_operations.Count == 0)
        {
            return [];
        }

        IReadOnlyList<string?> results = await _database.RunAsync(_stores, true, [.. _operations], cancellationToken).ConfigureAwait(false);
        return [.. results.Select(result => result is null ? (KazoWebDbKey?)null : KazoWebDbKey.FromJson(result))];
    }

    internal KazoWebDbTransaction Queue(KazoWebDbOperation operation)
    {
        if (_committed)
        {
            throw new InvalidOperationException("The transaction is already committed.");
        }

        _operations.Add(operation);
        return this;
    }

    internal KazoWebDb Database => _database;
}

/// <summary>A store inside a <see cref="KazoWebDbTransaction"/>: each call queues one write.</summary>
/// <typeparam name="T">The value type.</typeparam>
public sealed class KazoWebDbTransactionStore<T>
{
    private readonly KazoWebDbTransaction _transaction;
    private readonly string _name;

    internal KazoWebDbTransactionStore(KazoWebDbTransaction transaction, string name)
    {
        _transaction = transaction;
        _name = name;
    }

    /// <summary>Queues an <c>add</c>.</summary>
    public KazoWebDbTransactionStore<T> Add(T value, KazoWebDbKey? key = null)
    {
        _transaction.Queue(KazoWebDbStore<T>.WriteOperation(_transaction.Database, KazoWebDbOperationKinds.Add, _name, value, key));
        return this;
    }

    /// <summary>Queues a <c>put</c>.</summary>
    public KazoWebDbTransactionStore<T> Put(T value, KazoWebDbKey? key = null)
    {
        _transaction.Queue(KazoWebDbStore<T>.WriteOperation(_transaction.Database, KazoWebDbOperationKinds.Put, _name, value, key));
        return this;
    }

    /// <summary>Queues a <c>delete</c> of one key.</summary>
    public KazoWebDbTransactionStore<T> Delete(KazoWebDbKey key)
    {
        _transaction.Queue(new KazoWebDbOperation(KazoWebDbOperationKinds.Delete, _name, Key: key.Json));
        return this;
    }

    /// <summary>Queues a <c>clear</c>.</summary>
    public KazoWebDbTransactionStore<T> Clear()
    {
        _transaction.Queue(new KazoWebDbOperation(KazoWebDbOperationKinds.Clear, _name));
        return this;
    }
}

/// <summary>Registers a site's database.</summary>
public static class KazoWebDbServiceCollectionExtensions
{
    /// <summary>Name of the in-memory database a site gets when it runs outside Kazo OS.</summary>
    public const string InMemoryName = "hackweb.in-memory";

    /// <summary>
    /// Registers <see cref="KazoWebDb"/>, the site's browser database <c>hackweb.&lt;app id&gt;</c>, with its
    /// versions. It opens on first use. When the services hold an <see cref="IAppWebDatabaseGateway"/>
    /// (a test's <see cref="KazoWebDbInMemoryBackend"/>), it is used instead of the OS; a site built
    /// outside Kazo OS without one gets an in-memory database (<see cref="InMemoryName"/>).
    /// </summary>
    /// <param name="services">The site's services.</param>
    /// <param name="schema">Declares the versions (<see cref="KazoWebDbSchemaBuilder.Version"/>).</param>
    public static IServiceCollection AddHackWebDb(this IServiceCollection services, Action<KazoWebDbSchemaBuilder> schema)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(schema);
        KazoWebDbSchemaBuilder builder = new();
        schema(builder);
        // Fails at start-up, not at the first request, when the versions are wrong.
        _ = builder.Build();

        // The site's own process context, taken when the site builds its services (in its process).
        IAppWebDatabaseGateway? fromOs = KazoExecutionContext.CurrentOrNull?.WebDatabase;
        // Outside Kazo OS (a site's unit tests run KazoWebHost.CreateApplication), the database is in
        // memory and lives as long as the application.
        services.TryAddSingleton(provider => new KazoWebDb(
            provider.GetService<IAppWebDatabaseGateway>()
                ?? fromOs
                ?? new KazoWebDbInMemoryBackend(InMemoryName),
            builder,
            provider.GetService<KazoWebJsonOptions>() ?? new KazoWebJsonOptions()));
        return services;
    }
}
