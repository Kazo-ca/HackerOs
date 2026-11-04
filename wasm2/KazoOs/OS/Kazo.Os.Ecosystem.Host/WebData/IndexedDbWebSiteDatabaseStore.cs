using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.AppSdk.WebData;
using Kazo.Os.Infrastructure.Browser.WebData;
using Kazo.Os.Platform.Core.WebData;
using Microsoft.JSInterop;

namespace Kazo.Os.Ecosystem.WebData;

/// <summary>
/// <see cref="IWebSiteDatabaseStore"/> over the host browser's IndexedDB (ADR 0088, section 1), through
/// <see cref="WebSiteDatabaseInterop"/>. Remembers each site's schema, so a database deleted while its
/// site runs (Diagnostic, uninstall) comes back empty on the next call instead of failing.
/// </summary>
internal sealed class IndexedDbWebSiteDatabaseStore(WebSiteDatabaseInterop interop) : IWebSiteDatabaseStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _schemas = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async ValueTask OpenAsync(string databaseName, HackWebDbSchemaDefinition schema, CancellationToken cancellationToken = default)
    {
        WebSiteDatabaseNames.Require(databaseName);
        ArgumentNullException.ThrowIfNull(schema);
        string json = JsonSerializer.Serialize(schema, HackWebDbJsonContext.Default.HackWebDbSchemaDefinition);
        await Translate(() => interop.OpenAsync(databaseName, json, cancellationToken)).ConfigureAwait(false);
        lock (_gate)
        {
            _schemas[databaseName] = json;
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string?>> ExecuteAsync(string databaseName, HackWebDbRequest request, CancellationToken cancellationToken = default)
    {
        WebSiteDatabaseNames.Require(databaseName);
        ArgumentNullException.ThrowIfNull(request);
        string json = JsonSerializer.Serialize(request, HackWebDbJsonContext.Default.HackWebDbRequest);
        string results;
        try
        {
            results = await interop.ExecuteAsync(databaseName, json, cancellationToken).ConfigureAwait(false);
        }
        catch (JSException exception) when (exception.Message.Contains("is not open", StringComparison.Ordinal) && SchemaOf(databaseName) is { } schema)
        {
            // The connection was closed by a deletion: reopen with the site's schema, then retry once.
            await Translate(() => interop.OpenAsync(databaseName, schema, cancellationToken)).ConfigureAwait(false);
            results = await Translate(() => interop.ExecuteAsync(databaseName, json, cancellationToken)).ConfigureAwait(false);
        }
        catch (JSException exception)
        {
            throw HackWebDbException.FromMessage(exception.Message);
        }

        return JsonSerializer.Deserialize(results, HackWebDbJsonContext.Default.ListString) ?? [];
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HackWebSiteDatabaseInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        string[] known;
        lock (_gate)
        {
            known = [.. _schemas.Keys];
        }

        string json = await interop.ListAsync(JsonSerializer.Serialize(known, WebSiteDatabaseListJsonContext.Default.StringArray), cancellationToken).ConfigureAwait(false);
        List<WebSiteDatabaseListEntry> entries = JsonSerializer.Deserialize(json, WebSiteDatabaseListJsonContext.Default.ListWebSiteDatabaseListEntry) ?? [];
        return [.. entries.Select(entry => new HackWebSiteDatabaseInfo(
            entry.Name,
            HackWebDbNames.AppIdOf(entry.Name) ?? entry.Name,
            entry.Version,
            [.. entry.Stores.Select(store => new HackWebSiteDatabaseStoreInfo(store.Name, store.RecordCount))],
            entry.SizeBytes))];
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        WebSiteDatabaseNames.Require(databaseName);
        await Translate(() => interop.DeleteAsync(databaseName, cancellationToken)).ConfigureAwait(false);
    }

    private string? SchemaOf(string databaseName)
    {
        lock (_gate)
        {
            return _schemas.GetValueOrDefault(databaseName);
        }
    }

    private static async ValueTask Translate(Func<ValueTask> call)
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (JSException exception)
        {
            throw HackWebDbException.FromMessage(exception.Message);
        }
    }

    private static async ValueTask<T> Translate<T>(Func<ValueTask<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (JSException exception)
        {
            throw HackWebDbException.FromMessage(exception.Message);
        }
    }
}

/// <summary>One database as <c>webSiteDatabases.js</c> lists it.</summary>
internal sealed record WebSiteDatabaseListEntry(string Name, int Version, List<WebSiteDatabaseListStore> Stores, long SizeBytes);

/// <summary>One store as <c>webSiteDatabases.js</c> lists it.</summary>
internal sealed record WebSiteDatabaseListStore(string Name, int RecordCount);

/// <summary>JSON of the module's listing (trim-safe).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<WebSiteDatabaseListEntry>))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class WebSiteDatabaseListJsonContext : JsonSerializerContext;
