using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Administration;
using HackerOs.AppSdk.WebData;

namespace HackerOs.Platform.Core.WebData;

/// <summary>
/// Where the browser databases of web sites live (ADR 0088, section 1): the host browser's IndexedDB
/// (<c>IndexedDbWebSiteDatabaseStore</c> of <c>Infrastructure.Browser</c>), or memory on a host without it.
/// Every name it accepts starts with <see cref="HackWebDbNames.Prefix"/>; the OS database is out of reach.
/// </summary>
public interface IWebSiteDatabaseStore
{
    /// <summary>Opens a site database at the schema's version and remembers the schema, to reopen it after a deletion.</summary>
    /// <param name="databaseName">A <c>hackweb.*</c> name.</param>
    /// <param name="schema">The site's schema.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask OpenAsync(string databaseName, HackWebDbSchemaDefinition schema, CancellationToken cancellationToken = default);

    /// <summary>Runs one batch in one transaction.</summary>
    /// <param name="databaseName">A <c>hackweb.*</c> name.</param>
    /// <param name="request">The batch.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask<IReadOnlyList<string?>> ExecuteAsync(string databaseName, HackWebDbRequest request, CancellationToken cancellationToken = default);

    /// <summary>Lists every site database with its stores and size.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<IReadOnlyList<HackWebSiteDatabaseInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes a site database (no error when it does not exist).</summary>
    /// <param name="databaseName">A <c>hackweb.*</c> name.</param>
    /// <param name="cancellationToken">Cancels the deletion.</param>
    ValueTask DeleteAsync(string databaseName, CancellationToken cancellationToken = default);
}

/// <summary>Checks site database names.</summary>
public static class WebSiteDatabaseNames
{
    /// <summary>Throws when <paramref name="databaseName"/> is not <c>hackweb.&lt;something&gt;</c>.</summary>
    /// <param name="databaseName">The name to check.</param>
    /// <exception cref="ArgumentException">Not a site database name.</exception>
    public static void Require(string databaseName)
    {
        if (HackWebDbNames.AppIdOf(databaseName) is null)
        {
            throw new ArgumentException($"'{databaseName}' is not a site database name ('{HackWebDbNames.Prefix}<app id>').", nameof(databaseName));
        }
    }
}

/// <summary>
/// <see cref="IWebSiteDatabaseStore"/> in memory (<see cref="InMemoryWebDatabase"/>): for hosts without
/// IndexedDB and for tests. The databases last as long as the store.
/// </summary>
public sealed class InMemoryWebSiteDatabaseStore : IWebSiteDatabaseStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, InMemoryWebDatabase> _databases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HackWebDbSchemaDefinition> _schemas = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask OpenAsync(string databaseName, HackWebDbSchemaDefinition schema, CancellationToken cancellationToken = default)
    {
        WebSiteDatabaseNames.Require(databaseName);
        ArgumentNullException.ThrowIfNull(schema);
        Get(databaseName, create: true)!.Open(schema);
        lock (_gate)
        {
            _schemas[databaseName] = schema;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string?>> ExecuteAsync(string databaseName, HackWebDbRequest request, CancellationToken cancellationToken = default)
    {
        WebSiteDatabaseNames.Require(databaseName);
        InMemoryWebDatabase database = Get(databaseName, create: true)!;
        HackWebDbSchemaDefinition? schema;
        lock (_gate)
        {
            _schemas.TryGetValue(databaseName, out schema);
        }

        // Deleted while the site ran: like the browser module, reopen it with the schema the site gave.
        if (database.Version == 0 && schema is not null)
        {
            database.Open(schema);
        }

        return ValueTask.FromResult(database.Execute(request));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<HackWebSiteDatabaseInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        List<InMemoryWebDatabase> databases;
        lock (_gate)
        {
            databases = [.. _databases.Values.Where(database => database.Version > 0).OrderBy(database => database.Name, StringComparer.Ordinal)];
        }

        return ValueTask.FromResult<IReadOnlyList<HackWebSiteDatabaseInfo>>([.. databases.Select(database =>
        {
            (IReadOnlyList<(string Name, int Records)> stores, long size) = database.Describe();
            return new HackWebSiteDatabaseInfo(
                database.Name,
                HackWebDbNames.AppIdOf(database.Name)!,
                database.Version,
                [.. stores.Select(store => new HackWebSiteDatabaseStoreInfo(store.Name, store.Records))],
                size);
        })]);
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        WebSiteDatabaseNames.Require(databaseName);
        lock (_gate)
        {
            _databases.Remove(databaseName);
        }

        return ValueTask.CompletedTask;
    }

    private InMemoryWebDatabase? Get(string databaseName, bool create)
    {
        lock (_gate)
        {
            if (!_databases.TryGetValue(databaseName, out InMemoryWebDatabase? database) && create)
            {
                database = new InMemoryWebDatabase(databaseName);
                _databases[databaseName] = database;
            }

            return database;
        }
    }
}

/// <summary>
/// Trusted <see cref="IAppWebDatabaseGateway"/> of one app instance (ADR 0088, section 1): the database
/// name comes from the app's manifest, never from the caller, and only an app of kind
/// <see cref="AppKind.Web"/> may use it.
/// </summary>
internal sealed class AppWebDatabaseGateway : IAppWebDatabaseGateway
{
    private readonly IWebSiteDatabaseStore? _store;
    private readonly AppManifest _manifest;

    /// <summary>Creates the gateway of one app instance.</summary>
    /// <param name="store">The host's site databases; <see langword="null"/> on a host without any.</param>
    /// <param name="manifest">The app's manifest.</param>
    public AppWebDatabaseGateway(IWebSiteDatabaseStore? store, AppManifest manifest)
    {
        _store = store;
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        DatabaseName = HackWebDbNames.ForApp(manifest.Id);
    }

    /// <inheritdoc />
    public string DatabaseName { get; }

    /// <inheritdoc />
    public ValueTask OpenAsync(HackWebDbSchemaDefinition schema, CancellationToken cancellationToken = default) =>
        Store().OpenAsync(DatabaseName, schema, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string?>> ExecuteAsync(HackWebDbRequest request, CancellationToken cancellationToken = default) =>
        Store().ExecuteAsync(DatabaseName, request, cancellationToken);

    private IWebSiteDatabaseStore Store()
    {
        if (_manifest.Kind != AppKind.Web)
        {
            throw new UnauthorizedAccessException($"Only a web app has a browser database ('{_manifest.Id}' is a {_manifest.Kind} app).");
        }

        return _store ?? throw new NotSupportedException("This KazoOS host has no browser databases for sites.");
    }
}
