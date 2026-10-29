using System.Text.Json;
using System.Text.Json.Serialization;
using HackerOs.Infrastructure.Browser.Interop;
using HackerOs.Infrastructure.Browser.Schema;
using HackerOs.Simulation.Abstractions.Sessions;
using Microsoft.JSInterop;

namespace HackerOs.Infrastructure.Browser.Sessions;

/// <summary>Persists local groups in the canonical HackerOS IndexedDB database.</summary>
public sealed class IndexedDbLocalGroupRepository : ILocalGroupRepository, IAsyncDisposable
{
    private const string TransactionBoundary = "GroupWrite";
    private readonly IndexedDbInteropAdapter _adapter;

    /// <summary>Initializes a browser-backed local group repository.</summary>
    /// <param name="runtime">Browser JavaScript runtime used only by the internal storage adapter.</param>
    public IndexedDbLocalGroupRepository(IJSRuntime runtime)
    {
        _adapter = new IndexedDbInteropAdapter(runtime);
    }

    /// <inheritdoc />
    public async ValueTask<LocalGroup> CreateGroupAsync(
        LocalLoginName name,
        CancellationToken cancellationToken = default)
    {
        LocalGroup group = new(LocalGroupId.FromGuid(Guid.NewGuid()), name);
        LocalGroupRecord record = LocalGroupRecord.FromDomain(group);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _adapter.ExecuteAsync(
                TransactionBoundary,
                IndexedDbTransactionMode.ReadWrite,
                [new IndexedDbOperation("add", HackerOsIndexedDbSchema.GroupStoreName, Value: record)],
                cancellationToken).ConfigureAwait(false);
        }
        catch (JSException exception) when (exception.Message.Contains("ConstraintError", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Group name '{name}' is already in use.", exception);
        }

        return group;
    }

    /// <inheritdoc />
    public async ValueTask<LocalGroup?> FindByIdAsync(
        LocalGroupId id,
        CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadOnly,
            [new IndexedDbOperation("get", HackerOsIndexedDbSchema.GroupStoreName, Key: id.ToString())],
            cancellationToken).ConfigureAwait(false);

        JsonElement result = results.Single();
        return result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : LocalGroupRecord.ToDomain(result);
    }

    /// <inheritdoc />
    public async ValueTask<LocalGroup?> FindByNameAsync(
        LocalLoginName name,
        CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadOnly,
            [new IndexedDbOperation(
                "get",
                HackerOsIndexedDbSchema.GroupStoreName,
                Key: name.Value,
                IndexName: "name")],
            cancellationToken).ConfigureAwait(false);

        JsonElement result = results.Single();
        return result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : LocalGroupRecord.ToDomain(result);
    }

    /// <inheritdoc />
    public async ValueTask<LocalGroup> RenameGroupAsync(
        LocalGroupId id,
        LocalLoginName name,
        CancellationToken cancellationToken = default)
    {
        if (await FindByIdAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException($"Local group '{id}' does not exist.");
        }

        LocalGroup renamed = new(id, name);
        try
        {
            await _adapter.ExecuteAsync(
                TransactionBoundary,
                IndexedDbTransactionMode.ReadWrite,
                [new IndexedDbOperation("put", HackerOsIndexedDbSchema.GroupStoreName, Value: LocalGroupRecord.FromDomain(renamed))],
                cancellationToken).ConfigureAwait(false);
        }
        catch (JSException exception) when (exception.Message.Contains("ConstraintError", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Group name '{name}' is already in use.", exception);
        }

        return renamed;
    }

    /// <inheritdoc />
    public async ValueTask DeleteGroupAsync(LocalGroupId id, CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadWrite,
            [new IndexedDbOperation("delete", HackerOsIndexedDbSchema.GroupStoreName, Key: id.ToString())],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _adapter.DisposeAsync();

    private ValueTask EnsureOpenAsync(CancellationToken cancellationToken) =>
        _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken);

    private sealed record LocalGroupRecord(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name)
    {
        internal static LocalGroupRecord FromDomain(LocalGroup group) =>
            new(group.Id.ToString(), group.Name.Value);

        internal static LocalGroup ToDomain(JsonElement element) => new(
            LocalGroupId.FromGuid(Guid.ParseExact(element.GetProperty("id").GetString()!, "N")),
            LocalLoginName.Parse(element.GetProperty("name").GetString()!));
    }
}
