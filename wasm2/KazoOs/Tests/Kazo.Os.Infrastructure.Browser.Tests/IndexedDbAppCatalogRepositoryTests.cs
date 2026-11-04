using System.Text;
using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Infrastructure.Browser.Catalog;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>Verifies authoritative manifest reconciliation and durable local enablement.</summary>
public sealed class IndexedDbAppCatalogRepositoryTests
{
    [Fact]
    public async Task Reconcile_replaces_selected_snapshot_preserves_disable_and_retains_removed_disabled()
    {
        AppManifest selectedV1 = LoadCanonicalManifest();
        AppManifest removed = selectedV1 with
        {
            Id = "org.hackeros.removed",
            Name = "Removed App"
        };
        AppManifest selectedV2 = selectedV1 with { Version = "1.1.0", Description = "Updated build snapshot." };
        ManifestValidationResult removedValidation = AppManifestValidator.Validate(removed);
        Assert.True(
            removedValidation.IsValid,
            string.Join(Environment.NewLine, removedValidation.Errors.Select(error => $"{error.Code}: {error.Path}")));
        FakeIndexedDbModule module = new();
        await using IndexedDbAppCatalogRepository repository = new(new FakeJsRuntime(module));

        await repository.ReconcileAsync([selectedV1, removed]);
        Assert.True(await repository.SetEnabledAsync(selectedV1.Id, enabled: false));
        IReadOnlyList<PersistedAppCatalogEntry> runtime = await repository.ReconcileAsync([selectedV2]);
        IReadOnlyList<PersistedAppCatalogEntry> persisted = await repository.ReadAllAsync();

        PersistedAppCatalogEntry runtimeEntry = Assert.Single(runtime);
        Assert.Equal("1.1.0", runtimeEntry.Manifest.Version);
        Assert.False(runtimeEntry.IsEnabled);
        Assert.Equal([removed.Id, selectedV1.Id], persisted.Select(entry => entry.Manifest.Id));
        Assert.All(persisted, entry => Assert.False(entry.IsEnabled));
        Assert.Equal("1.1.0", persisted.Single(entry => entry.Manifest.Id == selectedV1.Id).Manifest.Version);

        TransactionInvocation finalReconciliation = module.Invocations
            .Where(invocation => invocation.Mode == "readwrite")
            .Last();
        Assert.Equal(["put", "put"], finalReconciliation.Operations.Select(operation => operation.Kind));
        Assert.All(finalReconciliation.Operations, operation =>
            Assert.Equal(KazoOsIndexedDbSchema.CatalogStoreName, operation.ObjectStoreName));
    }

    [Fact]
    public async Task Reconcile_rejects_duplicate_selected_app_ids_before_opening_indexeddb()
    {
        AppManifest manifest = LoadCanonicalManifest();
        FakeIndexedDbModule module = new();
        await using IndexedDbAppCatalogRepository repository = new(new FakeJsRuntime(module));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await repository.ReconcileAsync([manifest, manifest]));

        Assert.Empty(module.Invocations);
    }

    [Fact]
    public async Task Reconcile_does_not_rewrite_an_unchanged_catalog_snapshot()
    {
        AppManifest manifest = LoadCanonicalManifest();
        FakeIndexedDbModule module = new();
        await using IndexedDbAppCatalogRepository repository = new(new FakeJsRuntime(module));

        await repository.ReconcileAsync([manifest]);
        int writesAfterFirstReconciliation = module.Invocations.Count(invocation => invocation.Mode == "readwrite");
        await repository.ReconcileAsync([manifest]);

        Assert.Equal(1, writesAfterFirstReconciliation);
        Assert.Equal(
            writesAfterFirstReconciliation,
            module.Invocations.Count(invocation => invocation.Mode == "readwrite"));
    }

    private static AppManifest LoadCanonicalManifest()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Kazo.Os.sln")))
            {
                string path = Path.Combine(
                    directory,
                    "Shared",
                    "Kazo.Os.App.Abstractions",
                    "Schema",
                    "Fixtures",
                    "app-manifest.canonical.json");
                AppManifest manifest = AppManifestJsonSerializer.DeserializeStrict(File.ReadAllText(path, Encoding.UTF8));
                return manifest with
                {
                    Assets =
                    [
                        .. manifest.Assets,
                        new AssetManifest(
                            "schemas/open.json",
                            AssetKind.Data,
                            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")
                    ]
                };
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("Could not locate the canonical app manifest fixture.");
    }

    private sealed class FakeJsRuntime(IJSObjectReference module) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) => ValueTask.FromResult((TValue)module);
    }

    private sealed class FakeIndexedDbModule : IJSObjectReference
    {
        private readonly Dictionary<string, JsonElement> _records = new(StringComparer.Ordinal);

        internal List<TransactionInvocation> Invocations { get; } = [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            if (identifier != "executeTransaction")
            {
                return ValueTask.FromResult(default(TValue)!);
            }

            object?[] arguments = args ?? [];
            string mode = Assert.IsType<string>(arguments[3]);
            IReadOnlyList<IndexedDbOperation> operations =
                Assert.IsAssignableFrom<IReadOnlyList<IndexedDbOperation>>(arguments[4]);
            Invocations.Add(new TransactionInvocation(mode, operations));
            JsonElement[] results = operations.Select(Execute).ToArray();
            return ValueTask.FromResult((TValue)(object)results);
        }

        private JsonElement Execute(IndexedDbOperation operation)
        {
            Assert.Equal(KazoOsIndexedDbSchema.CatalogStoreName, operation.ObjectStoreName);
            if (operation.Kind == "getAll")
            {
                return JsonSerializer.SerializeToElement(_records.Values);
            }

            if (operation.Kind == "get")
            {
                return _records.TryGetValue((string)operation.Key!, out JsonElement record)
                    ? record
                    : JsonDocument.Parse("null").RootElement.Clone();
            }

            Assert.Equal("put", operation.Kind);
            JsonElement value = JsonSerializer.SerializeToElement(
                operation.Value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            _records[value.GetProperty("appId").GetString()!] = value;
            return JsonSerializer.SerializeToElement(value.GetProperty("appId").GetString());
        }
    }

    private sealed record TransactionInvocation(
        string Mode,
        IReadOnlyList<IndexedDbOperation> Operations);
}
