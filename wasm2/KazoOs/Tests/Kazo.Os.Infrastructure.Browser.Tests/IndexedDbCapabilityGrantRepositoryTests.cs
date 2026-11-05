using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Policy;
using Kazo.Os.Infrastructure.Browser.Schema;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>Verifies atomic durable capability policy mutations and reload behavior.</summary>
public sealed class IndexedDbCapabilityGrantRepositoryTests
{
    [Fact]
    public async Task Grant_and_revoke_commit_grant_audit_and_revision_then_reload_revoked()
    {
        FakeIndexedDbModule module = new();
        await using IndexedDbCapabilityGrantRepository repository = new(
            new FakeJsRuntime(module),
            new FixedTimeProvider(),
            new GuidSequence());

        CapabilityGrantMutationResult granted = await repository.GrantAsync(
            "org.kazoos.browser",
            "user-1",
            AppCapabilities.FileSystemUserHomeRead,
            CapabilityGrantSource.AdministratorApproval,
            AppAuthority.Administrator,
            [new VirtualPathCapabilityConstraint(VirtualPath.Parse("/home/user-1"), true)]);
        CapabilityGrantMutationResult revoked = await repository.RevokeAsync(
            granted.Grant!.Id,
            AppAuthority.Administrator);
        CapabilityPolicyEvaluation evaluation = await repository.EvaluateAsync(
            "org.kazoos.browser",
            "user-1",
            AppCapabilities.FileSystemUserHomeRead,
            AppAuthority.User,
            AppAuthority.User,
            new VirtualPathResourceCandidate(VirtualPath.Parse("/home/user-1/file.txt")));

        Assert.Equal(CapabilityGrantMutationStatus.Granted, granted.Status);
        Assert.Equal(1, granted.PolicyRevision);
        Assert.Equal(CapabilityGrantMutationStatus.Revoked, revoked.Status);
        Assert.Equal(2, revoked.PolicyRevision);
        Assert.Equal(CapabilityPolicyEvaluationReason.Revoked, evaluation.Reason);
        Assert.Equal(2, await repository.GetCurrentPolicyRevisionAsync());

        TransactionInvocation[] mutations = module.Invocations
            .Where(invocation => invocation.Operations.Any(operation => operation.Kind == "assertPropertyEquals"))
            .ToArray();
        Assert.Equal(2, mutations.Length);
        Assert.All(mutations, invocation =>
        {
            Assert.Equal(
                [
                    KazoOsIndexedDbSchema.GrantStoreName,
                    KazoOsIndexedDbSchema.AuditStoreName,
                    KazoOsIndexedDbSchema.LocalBookkeepingStoreName
                ],
                invocation.StoreNames);
            Assert.Equal(
                ["assertPropertyEquals", "put", "add", "put"],
                invocation.Operations.Select(operation => operation.Kind));
        });
        Assert.Equal(["capability.grant", "capability.revoke"], module.AuditActions);
        JsonElement persistedGrant = Assert.IsType<JsonElement>(module.Grant);
        Assert.Equal(1785672000000, persistedGrant.GetProperty("revokedAtUtcMs").GetInt64());
        Assert.Equal(2, persistedGrant.GetProperty("revokedRevision").GetInt64());
    }

    [Fact]
    public async Task Import_creates_grant_under_the_given_id_with_a_distinct_audit_action()
    {
        FakeIndexedDbModule module = new();
        await using IndexedDbCapabilityGrantRepository repository = new(
            new FakeJsRuntime(module),
            new FixedTimeProvider(),
            new GuidSequence());
        CapabilityGrantId serverIssuedId = CapabilityGrantId.FromGuid(Guid.Parse("11111111-1111-1111-1111-111111111111"));

        CapabilityGrantMutationResult imported = await repository.ImportAsync(
            serverIssuedId,
            "org.kazoos.browser",
            "user-1",
            AppCapabilities.FileSystemUserHomeRead,
            CapabilityGrantSource.AdministratorApproval,
            constraints: null,
            isRevoked: false,
            AppAuthority.Administrator);

        Assert.Equal(CapabilityGrantMutationStatus.Granted, imported.Status);
        Assert.Equal(serverIssuedId, imported.Grant!.Id);
        Assert.Equal(["capability.sync-import"], module.AuditActions);
        JsonElement persistedGrant = Assert.IsType<JsonElement>(module.Grant);
        Assert.Equal(serverIssuedId.ToString(), persistedGrant.GetProperty("id").GetString());
        Assert.False(persistedGrant.TryGetProperty("revokedAtUtcMs", out JsonElement revokedAt) && revokedAt.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Import_revoked_sets_revocation_fields_and_reports_revoked_status()
    {
        FakeIndexedDbModule module = new();
        await using IndexedDbCapabilityGrantRepository repository = new(
            new FakeJsRuntime(module),
            new FixedTimeProvider(),
            new GuidSequence());
        CapabilityGrantId serverIssuedId = CapabilityGrantId.FromGuid(Guid.Parse("22222222-2222-2222-2222-222222222222"));

        CapabilityGrantMutationResult imported = await repository.ImportAsync(
            serverIssuedId,
            "org.kazoos.browser",
            "user-1",
            AppCapabilities.FileSystemUserHomeRead,
            CapabilityGrantSource.AdministratorApproval,
            constraints: null,
            isRevoked: true,
            AppAuthority.Administrator);

        Assert.Equal(CapabilityGrantMutationStatus.Revoked, imported.Status);
        JsonElement persistedGrant = Assert.IsType<JsonElement>(module.Grant);
        Assert.Equal(1785672000000, persistedGrant.GetProperty("revokedAtUtcMs").GetInt64());
        Assert.Equal(1, persistedGrant.GetProperty("revokedRevision").GetInt64());
    }

    [Fact]
    public async Task Import_same_id_twice_updates_in_place_instead_of_duplicating()
    {
        FakeIndexedDbModule module = new();
        await using IndexedDbCapabilityGrantRepository repository = new(
            new FakeJsRuntime(module),
            new FixedTimeProvider(),
            new GuidSequence());
        CapabilityGrantId serverIssuedId = CapabilityGrantId.FromGuid(Guid.Parse("33333333-3333-3333-3333-333333333333"));

        await repository.ImportAsync(
            serverIssuedId, "org.kazoos.browser", "user-1", AppCapabilities.FileSystemUserHomeRead,
            CapabilityGrantSource.AdministratorApproval, constraints: null, isRevoked: false, AppAuthority.Administrator);

        // A later re-import of the same server RecordId (e.g. the same grant, now revoked) must update
        // the existing row under the same id, not mint a second grant record.
        CapabilityGrantMutationResult second = await repository.ImportAsync(
            serverIssuedId, "org.kazoos.browser", "user-1", AppCapabilities.FileSystemUserHomeRead,
            CapabilityGrantSource.AdministratorApproval, constraints: null, isRevoked: true, AppAuthority.Administrator);

        Assert.Equal(serverIssuedId, second.Grant!.Id);
        Assert.Equal(2, second.PolicyRevision);
        JsonElement persistedGrant = Assert.IsType<JsonElement>(module.Grant);
        Assert.Equal(serverIssuedId.ToString(), persistedGrant.GetProperty("id").GetString());
        Assert.Equal(1785672000000, persistedGrant.GetProperty("revokedAtUtcMs").GetInt64());
        Assert.Equal(["capability.sync-import", "capability.sync-import"], module.AuditActions);
    }

    [Fact]
    public async Task Grant_reports_explicit_conflict_when_policy_revision_changes_before_commit()
    {
        FakeIndexedDbModule module = new() { ConflictOnNextAssertion = true };
        await using IndexedDbCapabilityGrantRepository repository = new(
            new FakeJsRuntime(module),
            new FixedTimeProvider(),
            new GuidSequence());

        await Assert.ThrowsAsync<CapabilityGrantConflictException>(async () =>
            await repository.GrantAsync(
                "org.kazoos.browser",
                "user-1",
                AppCapabilities.FileSystemUserHomeRead,
                CapabilityGrantSource.AdministratorApproval,
                AppAuthority.Administrator));

        Assert.Null(module.Grant);
        Assert.Empty(module.AuditActions);
        Assert.Equal(1, module.PolicyRevision);
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
        private JsonElement? _revision;
        private readonly List<JsonElement> _audit = [];

        internal bool ConflictOnNextAssertion { get; init; }
        internal JsonElement? Grant { get; private set; }
        internal long PolicyRevision => _revision?.GetProperty("value").GetInt64() ?? 0;
        internal IReadOnlyList<string> AuditActions =>
            _audit.Select(record => record.GetProperty("action").GetString()!).ToArray();
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
            IReadOnlyList<string> stores = Assert.IsAssignableFrom<IReadOnlyList<string>>(arguments[2]);
            IReadOnlyList<IndexedDbOperation> operations =
                Assert.IsAssignableFrom<IReadOnlyList<IndexedDbOperation>>(arguments[4]);
            Invocations.Add(new TransactionInvocation(stores, operations));

            if (ConflictOnNextAssertion
                && operations.Any(operation => operation.Kind == "assertPropertyEquals"))
            {
                _revision = JsonSerializer.SerializeToElement(new { key = "policyRevision", value = 1 });
                throw new JSException("policy.revision-conflict: 'value'.");
            }

            JsonElement[] results = operations.Select(Execute).ToArray();
            return ValueTask.FromResult((TValue)(object)results);
        }

        private JsonElement Execute(IndexedDbOperation operation)
        {
            if (operation.Kind == "addIfAbsent")
            {
                _revision ??= Serialize(operation.Value);
                return Serialize(new { added = true });
            }

            if (operation.Kind == "get")
            {
                if (operation.ObjectStoreName == KazoOsIndexedDbSchema.LocalBookkeepingStoreName)
                {
                    return _revision!.Value;
                }

                return Grant ?? JsonDocument.Parse("null").RootElement.Clone();
            }

            if (operation.Kind == "getAll")
            {
                object[] query = Assert.IsType<object[]>(operation.Query);
                bool matches = Grant is { } grant
                    && grant.GetProperty("appId").GetString() == (string)query[0]
                    && grant.GetProperty("userId").GetString() == (string)query[1]
                    && grant.GetProperty("capability").GetString() == (string)query[2];
                return JsonSerializer.SerializeToElement(matches ? new[] { Grant!.Value } : []);
            }

            if (operation.Kind == "assertPropertyEquals")
            {
                Assert.Equal(PolicyRevision, Convert.ToInt64(operation.ExpectedValue));
                return JsonSerializer.SerializeToElement(PolicyRevision);
            }

            if (operation.Kind == "put")
            {
                JsonElement value = Serialize(operation.Value);
                if (operation.ObjectStoreName == KazoOsIndexedDbSchema.GrantStoreName)
                {
                    Grant = value;
                }
                else
                {
                    _revision = value;
                }

                return JsonDocument.Parse("null").RootElement.Clone();
            }

            if (operation.Kind == "add")
            {
                _audit.Add(Serialize(operation.Value));
                return JsonSerializer.SerializeToElement(_audit.Count);
            }

            throw new InvalidOperationException($"Unexpected operation '{operation.Kind}'.");
        }

        private static JsonElement Serialize(object? value) => JsonSerializer.SerializeToElement(
            value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private sealed record TransactionInvocation(
        IReadOnlyList<string> StoreNames,
        IReadOnlyList<IndexedDbOperation> Operations);

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class GuidSequence
    {
        private int _value = 1;

        public Guid Next() => new(_value++, 0, 0, new byte[8]);

        public static implicit operator Func<Guid>(GuidSequence sequence) => sequence.Next;
    }
}