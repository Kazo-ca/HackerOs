using System.Text.Json.Serialization;

namespace Kazo.Os.AppSdk.WebData;

/// <summary>
/// The browser database of a web site (ADR 0073 §3, ADR 0088 §1): one IndexedDB database of the host
/// browser, named <c>hackweb.&lt;app id&gt;</c> by the OS. Behind <c>KazoWebDb</c> of the web SDK; only an
/// app of kind <c>Web</c> receives a working one.
/// </summary>
/// <remarks>
/// Values and keys cross this gateway as JSON text, so the same calls work in-process and from the
/// shared web worker (ADR 0070 §4). A transaction is a batch (<see cref="KazoWebDbRequest"/>): its
/// operations run in one IndexedDB transaction, in order, and succeed or fail together (ADR 0088 §2).
/// </remarks>
public interface IAppWebDatabaseGateway
{
    /// <summary>Gets the name of the database, <c>hackweb.&lt;app id&gt;</c>.</summary>
    string DatabaseName { get; }

    /// <summary>
    /// Opens the database at <paramref name="schema"/>'s version, applying its migration steps above
    /// the stored version, like <c>indexedDB.open(name, version)</c> and <c>onupgradeneeded</c>.
    /// </summary>
    /// <param name="schema">The versions and migration steps the site declares.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="KazoWebDbException"><c>VersionError</c>: the stored database is newer than <paramref name="schema"/>.</exception>
    ValueTask OpenAsync(KazoWebDbSchemaDefinition schema, CancellationToken cancellationToken = default);

    /// <summary>Runs a batch of operations in one transaction and returns one JSON result per operation.</summary>
    /// <param name="request">The batch.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// Per operation: the value (<c>get</c>), an array (<c>getAll</c>, <c>getAllKeys</c>), a number
    /// (<c>count</c>), the record's key (<c>add</c>, <c>put</c>), or <see langword="null"/>.
    /// </returns>
    /// <exception cref="KazoWebDbException">The transaction failed and nothing of it was written.</exception>
    ValueTask<IReadOnlyList<string?>> ExecuteAsync(KazoWebDbRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Names of the browser databases of sites.</summary>
public static class KazoWebDbNames
{
    /// <summary>The prefix every site database has; the OS opens no other name for a site.</summary>
    public const string Prefix = "hackweb.";

    /// <summary>Returns the database name of an app: <c>hackweb.&lt;appId&gt;</c>.</summary>
    /// <param name="appId">The app id.</param>
    public static string ForApp(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return Prefix + appId;
    }

    /// <summary>Returns the app id of a site database name, or <see langword="null"/> when it is not one.</summary>
    /// <param name="databaseName">A database name.</param>
    public static string? AppIdOf(string databaseName) =>
        databaseName is { Length: > 8 } && databaseName.StartsWith(Prefix, StringComparison.Ordinal) ? databaseName[Prefix.Length..] : null;
}

/// <summary>Names of the operations of a <see cref="KazoWebDbOperation"/>, as IndexedDB's methods.</summary>
public static class KazoWebDbOperationKinds
{
    /// <summary><c>IDBObjectStore.get</c> / <c>IDBIndex.get</c>: the first value matching a key or range.</summary>
    public const string Get = "get";

    /// <summary><c>getAll</c>: values in key order, optionally within a range and up to a count.</summary>
    public const string GetAll = "getAll";

    /// <summary><c>getAllKeys</c>: primary keys in key order.</summary>
    public const string GetAllKeys = "getAllKeys";

    /// <summary><c>count</c>: records matching a range (all when none).</summary>
    public const string Count = "count";

    /// <summary><c>add</c>: stores a new record; <c>ConstraintError</c> when the key exists.</summary>
    public const string Add = "add";

    /// <summary><c>put</c>: stores a record, replacing the one of the same key.</summary>
    public const string Put = "put";

    /// <summary><c>delete</c>: removes the records of a key or range.</summary>
    public const string Delete = "delete";

    /// <summary><c>clear</c>: removes every record of the store.</summary>
    public const string Clear = "clear";

    /// <summary>Whether <paramref name="kind"/> writes, so needs a <c>readwrite</c> transaction.</summary>
    public static bool IsWrite(string kind) => kind is Add or Put or Delete or Clear;

    /// <summary>Whether <paramref name="kind"/> is an operation this API knows.</summary>
    public static bool IsKnown(string kind) => kind is Get or GetAll or GetAllKeys or Count or Add or Put or Delete or Clear;
}

/// <summary>Every version a site database went through, as IndexedDB upgrade steps.</summary>
/// <param name="Version">The version to open; the highest step's target.</param>
/// <param name="Steps">One step per version from 1, in order.</param>
public sealed record KazoWebDbSchemaDefinition(int Version, IReadOnlyList<KazoWebDbMigrationStep> Steps);

/// <summary>What <c>onupgradeneeded</c> does to reach one version.</summary>
/// <param name="TargetVersion">The version this step reaches.</param>
/// <param name="CreateStores">Object stores to create (with their indexes).</param>
/// <param name="DeleteStores">Object stores to delete.</param>
/// <param name="CreateIndexes">Indexes to add to existing stores.</param>
/// <param name="DeleteIndexes">Indexes to remove (only the index name is read).</param>
public sealed record KazoWebDbMigrationStep(
    int TargetVersion,
    IReadOnlyList<KazoWebDbStoreDefinition> CreateStores,
    IReadOnlyList<string> DeleteStores,
    IReadOnlyList<KazoWebDbIndexChange> CreateIndexes,
    IReadOnlyList<KazoWebDbIndexChange> DeleteIndexes);

/// <summary>An object store (<c>createObjectStore</c>).</summary>
/// <param name="Name">Store name.</param>
/// <param name="KeyPath">A property path (<c>id</c>, <c>author.name</c>), or <see langword="null"/> for out-of-line keys.</param>
/// <param name="AutoIncrement">Whether the store generates numeric keys.</param>
/// <param name="Indexes">Indexes created with the store.</param>
public sealed record KazoWebDbStoreDefinition(string Name, string? KeyPath, bool AutoIncrement, IReadOnlyList<KazoWebDbIndexDefinition> Indexes);

/// <summary>An index (<c>createIndex</c>).</summary>
/// <param name="Name">Index name.</param>
/// <param name="KeyPath">The property path the index reads.</param>
/// <param name="Unique">Whether two records may not share an index key (<c>ConstraintError</c>).</param>
/// <param name="MultiEntry">Whether an array value adds one index entry per element.</param>
public sealed record KazoWebDbIndexDefinition(string Name, string KeyPath, bool Unique = false, bool MultiEntry = false);

/// <summary>An index added to, or removed from, an existing store.</summary>
/// <param name="Store">Store name.</param>
/// <param name="Index">The index.</param>
public sealed record KazoWebDbIndexChange(string Store, KazoWebDbIndexDefinition Index);

/// <summary>A transaction: its scope, its mode and its operations in order.</summary>
/// <param name="Version">The version the site opened (a transaction on another version is refused).</param>
/// <param name="Stores">The object stores in the transaction's scope.</param>
/// <param name="ReadWrite">A <c>readwrite</c> transaction; otherwise <c>readonly</c>.</param>
/// <param name="Operations">The operations.</param>
public sealed record KazoWebDbRequest(int Version, IReadOnlyList<string> Stores, bool ReadWrite, IReadOnlyList<KazoWebDbOperation> Operations);

/// <summary>One operation of a <see cref="KazoWebDbRequest"/>.</summary>
/// <param name="Kind">One of <see cref="KazoWebDbOperationKinds"/>.</param>
/// <param name="Store">The object store.</param>
/// <param name="Index">An index of the store, for reads through it.</param>
/// <param name="Key">A key as JSON (number, string or array), for <c>get</c>, <c>delete</c> or an out-of-line <c>add</c>/<c>put</c>.</param>
/// <param name="Range">A key range, for reads, <c>count</c> and <c>delete</c>.</param>
/// <param name="Count">The most records <c>getAll</c>/<c>getAllKeys</c> return.</param>
/// <param name="Value">The record as JSON, for <c>add</c>/<c>put</c>.</param>
public sealed record KazoWebDbOperation(
    string Kind,
    string Store,
    string? Index = null,
    string? Key = null,
    KazoWebDbRange? Range = null,
    int? Count = null,
    string? Value = null);

/// <summary>An <c>IDBKeyRange</c>: keys as JSON, a missing bound is open-ended.</summary>
/// <param name="Lower">Lower bound, or <see langword="null"/>.</param>
/// <param name="Upper">Upper bound, or <see langword="null"/>.</param>
/// <param name="LowerOpen">Whether the lower bound itself is excluded.</param>
/// <param name="UpperOpen">Whether the upper bound itself is excluded.</param>
public sealed record KazoWebDbRange(string? Lower, string? Upper, bool LowerOpen = false, bool UpperOpen = false);

/// <summary>
/// A failed site database call, named as IndexedDB's <c>DOMException</c>: <c>ConstraintError</c>,
/// <c>NotFoundError</c>, <c>DataError</c>, <c>VersionError</c>, <c>InvalidStateError</c>,
/// <c>QuotaExceededError</c>.
/// </summary>
public sealed class KazoWebDbException : Exception
{
    /// <summary>A key or unique index already holds the record.</summary>
    public const string ConstraintError = "ConstraintError";

    /// <summary>An unknown object store or index.</summary>
    public const string NotFoundError = "NotFoundError";

    /// <summary>An invalid key or a missing key.</summary>
    public const string DataError = "DataError";

    /// <summary>The stored database is newer than the version asked.</summary>
    public const string VersionError = "VersionError";

    /// <summary>The database is not open at this version, or the call is not valid now.</summary>
    public const string InvalidStateError = "InvalidStateError";

    /// <summary>The host browser's storage is full.</summary>
    public const string QuotaExceededError = "QuotaExceededError";

    /// <summary>Initializes the exception.</summary>
    /// <param name="name">The IndexedDB error name.</param>
    /// <param name="message">What went wrong.</param>
    public KazoWebDbException(string name, string message)
        : base($"{name}: {message}")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Gets the IndexedDB error name.</summary>
    public string Name { get; }

    /// <summary>Rebuilds the exception from a message <c>"Name: text"</c>, as a browser or the worker reports it.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The exception, named <see cref="InvalidStateError"/> when the message carries no known name.</returns>
    public static KazoWebDbException FromMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        foreach (string name in (string[])[ConstraintError, NotFoundError, DataError, VersionError, InvalidStateError, QuotaExceededError])
        {
            int at = message.IndexOf(name + ": ", StringComparison.Ordinal);
            if (at >= 0)
            {
                return new KazoWebDbException(name, message[(at + name.Length + 2)..]);
            }
        }

        return new KazoWebDbException(InvalidStateError, message);
    }
}

/// <summary>Source-generated JSON of the gateway's messages (the worker protocol and the browser module).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(KazoWebDbSchemaDefinition))]
[JsonSerializable(typeof(KazoWebDbRequest))]
[JsonSerializable(typeof(List<string?>))]
public sealed partial class KazoWebDbJsonContext : JsonSerializerContext;
