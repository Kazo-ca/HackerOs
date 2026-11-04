using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Policy;

/// <summary>Persists capability grants, revocations, audit, and policy revision atomically.</summary>
public sealed class IndexedDbCapabilityGrantRepository :
    IPersistentCapabilityGrantRepository,
    IAsyncDisposable
{
    private const string MutationBoundary = "PolicyGrantMutation";
    private const string BookkeepingBoundary = "LocalBookkeepingUpdate";
    private const string PolicyRevisionKey = "policyRevision";
    private readonly IndexedDbInteropAdapter _adapter;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Guid> _guidFactory;

    /// <summary>Initializes a browser-backed capability grant repository.</summary>
    public IndexedDbCapabilityGrantRepository(
        IJSRuntime runtime,
        TimeProvider? timeProvider = null,
        Func<Guid>? guidFactory = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _adapter = new IndexedDbInteropAdapter(runtime);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _guidFactory = guidFactory ?? Guid.NewGuid;
    }

    /// <inheritdoc />
    public async ValueTask<long> GetCurrentPolicyRevisionAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRevisionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<CapabilityGrantMutationResult> GrantAsync(
        string appId,
        string userId,
        string capability,
        CapabilityGrantSource source,
        AppAuthority actingAuthority,
        IEnumerable<CapabilityConstraint>? constraints = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        long revision = await ReadRevisionAsync(cancellationToken).ConfigureAwait(false);
        if (!AppAuthorityPolicy.Satisfies(actingAuthority, AppAuthority.Administrator))
        {
            return new(CapabilityGrantMutationStatus.AuthorityDenied, null, revision);
        }

        CapabilityConstraint[] copiedConstraints = constraints?.ToArray() ?? [];
        IReadOnlyList<GrantRecord> existing = await ReadMatchingAsync(
            appId, userId, capability, cancellationToken).ConfigureAwait(false);
        bool isExpansion = existing
            .Where(static record => record.RevokedAtUtcMs is null)
            .Select(static record => record.ToDomain())
            .Any(grant => IsBroaderOrEqual(copiedConstraints, grant.Constraints)
                && !IsBroaderOrEqual(grant.Constraints, copiedConstraints));

        long nextRevision = checked(revision + 1);
        CapabilityGrant grant = new(
            CapabilityGrantId.FromGuid(_guidFactory()),
            appId,
            userId,
            capability,
            nextRevision,
            source,
            copiedConstraints);
        DateTimeOffset timestamp = _timeProvider.GetUtcNow().ToUniversalTime();
        string action = isExpansion ? "capability.expand" : "capability.grant";
        await CommitMutationAsync(
            revision,
            GrantRecord.FromDomain(grant),
            AuditRecord.Create(action, grant, nextRevision, actingAuthority, timestamp, _guidFactory()),
            cancellationToken).ConfigureAwait(false);

        return new(
            isExpansion ? CapabilityGrantMutationStatus.Expanded : CapabilityGrantMutationStatus.Granted,
            grant,
            nextRevision);
    }

    /// <inheritdoc />
    public async ValueTask<CapabilityGrantMutationResult> RevokeAsync(
        CapabilityGrantId grantId,
        AppAuthority actingAuthority,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        (long revision, GrantRecord? record) = await ReadRevisionAndGrantAsync(
            grantId, cancellationToken).ConfigureAwait(false);
        if (!AppAuthorityPolicy.Satisfies(actingAuthority, AppAuthority.Administrator))
        {
            return new(CapabilityGrantMutationStatus.AuthorityDenied, null, revision);
        }

        if (record is null || record.RevokedAtUtcMs is not null)
        {
            return new(CapabilityGrantMutationStatus.NotFound, null, revision);
        }

        long nextRevision = checked(revision + 1);
        DateTimeOffset timestamp = _timeProvider.GetUtcNow().ToUniversalTime();
        CapabilityGrant grant = record.ToDomain();
        GrantRecord revoked = record with
        {
            RevokedAtUtcMs = timestamp.ToUnixTimeMilliseconds(),
            RevokedRevision = nextRevision
        };
        await CommitMutationAsync(
            revision,
            revoked,
            AuditRecord.Create(
                "capability.revoke",
                grant,
                nextRevision,
                actingAuthority,
                timestamp,
                _guidFactory()),
            cancellationToken).ConfigureAwait(false);

        return new(CapabilityGrantMutationStatus.Revoked, grant, nextRevision);
    }

    /// <inheritdoc />
    public async ValueTask<CapabilityGrantMutationResult> ImportAsync(
        CapabilityGrantId id,
        string appId,
        string userId,
        string capability,
        CapabilityGrantSource source,
        IEnumerable<CapabilityConstraint>? constraints,
        bool isRevoked,
        AppAuthority actingAuthority,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        (long revision, GrantRecord? _) = await ReadRevisionAndGrantAsync(id, cancellationToken).ConfigureAwait(false);
        if (!AppAuthorityPolicy.Satisfies(actingAuthority, AppAuthority.Administrator))
        {
            return new(CapabilityGrantMutationStatus.AuthorityDenied, null, revision);
        }

        long nextRevision = checked(revision + 1);
        CapabilityGrant grant = new(
            id, appId, userId, capability, nextRevision, source, constraints?.ToArray() ?? []);
        DateTimeOffset timestamp = _timeProvider.GetUtcNow().ToUniversalTime();
        GrantRecord record = GrantRecord.FromDomain(grant) with
        {
            RevokedAtUtcMs = isRevoked ? timestamp.ToUnixTimeMilliseconds() : null,
            RevokedRevision = isRevoked ? nextRevision : null
        };

        // "capability.sync-import" is a distinct audit action from "capability.grant"/"capability.revoke"
        // so the audit trail can distinguish a server-issued sync application from a locally-authorized
        // grant/revoke mutation (ADR 0031). Upserts by id — create-or-update-in-place, unlike GrantAsync's
        // always-mint-a-new-ID behavior, so a later re-import of the same RecordId (e.g. a revocation)
        // updates this row instead of creating a duplicate.
        await CommitMutationAsync(
            revision,
            record,
            AuditRecord.Create("capability.sync-import", grant, nextRevision, actingAuthority, timestamp, _guidFactory()),
            cancellationToken).ConfigureAwait(false);

        return new(
            isRevoked ? CapabilityGrantMutationStatus.Revoked : CapabilityGrantMutationStatus.Granted,
            grant,
            nextRevision);
    }

    /// <inheritdoc />
    public async ValueTask<CapabilityPolicyEvaluation> EvaluateAsync(
        string appId,
        string userId,
        string capability,
        AppAuthority actingAuthority,
        AppAuthority requiredAuthority,
        CapabilityResourceCandidate? resourceCandidate = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        long revision = await ReadRevisionAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<GrantRecord> records = await ReadMatchingAsync(
            appId, userId, capability, cancellationToken).ConfigureAwait(false);
        CapabilityGrant[] active = records
            .Where(static record => record.RevokedAtUtcMs is null)
            .Select(static record => record.ToDomain())
            .ToArray();
        CapabilityGrant? matching = active.FirstOrDefault(grant => Allows(grant, resourceCandidate));
        if (matching is not null)
        {
            return AppAuthorityPolicy.Satisfies(actingAuthority, requiredAuthority)
                ? CapabilityPolicyEvaluation.Permit(matching)
                : CapabilityPolicyEvaluation.Deny(matching, CapabilityPolicyEvaluationReason.AuthorityDenied);
        }

        if (active.FirstOrDefault() is { } constrained)
        {
            return CapabilityPolicyEvaluation.Deny(constrained, CapabilityPolicyEvaluationReason.Constrained);
        }

        if (records.FirstOrDefault(static record => record.RevokedAtUtcMs is not null) is { } revoked)
        {
            return CapabilityPolicyEvaluation.Deny(
                revoked.ToDomain(), CapabilityPolicyEvaluationReason.Revoked);
        }

        return CapabilityPolicyEvaluation.DenyMissing(Math.Max(revision, 1));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _adapter.DisposeAsync();

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken)
            .ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            BookkeepingBoundary,
            IndexedDbTransactionMode.ReadWrite,
            [new IndexedDbOperation(
                "addIfAbsent",
                HackerOsIndexedDbSchema.LocalBookkeepingStoreName,
                Key: PolicyRevisionKey,
                Value: new RevisionRecord(PolicyRevisionKey, 0))],
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<long> ReadRevisionAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            MutationBoundary,
            IndexedDbTransactionMode.ReadOnly,
            [new IndexedDbOperation(
                "get",
                HackerOsIndexedDbSchema.LocalBookkeepingStoreName,
                Key: PolicyRevisionKey)],
            cancellationToken).ConfigureAwait(false);
        return ReadRevision(results.Single());
    }

    private async ValueTask<(long Revision, GrantRecord? Grant)> ReadRevisionAndGrantAsync(
        CapabilityGrantId grantId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            MutationBoundary,
            IndexedDbTransactionMode.ReadOnly,
            [
                new IndexedDbOperation(
                    "get",
                    HackerOsIndexedDbSchema.LocalBookkeepingStoreName,
                    Key: PolicyRevisionKey),
                new IndexedDbOperation(
                    "get",
                    HackerOsIndexedDbSchema.GrantStoreName,
                    Key: grantId.ToString())
            ],
            cancellationToken).ConfigureAwait(false);
        JsonElement grant = results[1];
        return (
            ReadRevision(results[0]),
            grant.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? null
                : GrantRecord.FromJson(grant));
    }

    private async ValueTask<IReadOnlyList<GrantRecord>> ReadMatchingAsync(
        string appId,
        string userId,
        string capability,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            MutationBoundary,
            IndexedDbTransactionMode.ReadOnly,
            [new IndexedDbOperation(
                "getAll",
                HackerOsIndexedDbSchema.GrantStoreName,
                IndexName: "appUserCapability",
                Query: new object[] { appId, userId, capability })],
            cancellationToken).ConfigureAwait(false);
        JsonElement records = results.Single();
        if (records.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Persisted capability grants must be returned as an array.");
        }

        return records.EnumerateArray().Select(GrantRecord.FromJson).ToArray();
    }

    private async ValueTask CommitMutationAsync(
        long expectedRevision,
        GrantRecord grant,
        AuditRecord audit,
        CancellationToken cancellationToken)
    {
        try
        {
            await _adapter.ExecuteAsync(
                MutationBoundary,
                IndexedDbTransactionMode.ReadWrite,
                [
                    new IndexedDbOperation(
                        "assertPropertyEquals",
                        HackerOsIndexedDbSchema.LocalBookkeepingStoreName,
                        Key: PolicyRevisionKey,
                        CompareProperty: "value",
                        ExpectedValue: expectedRevision,
                        FailureCode: "policy.revision-conflict"),
                    new IndexedDbOperation(
                        "put",
                        HackerOsIndexedDbSchema.GrantStoreName,
                        Value: grant),
                    new IndexedDbOperation(
                        "add",
                        HackerOsIndexedDbSchema.AuditStoreName,
                        Value: audit),
                    new IndexedDbOperation(
                        "put",
                        HackerOsIndexedDbSchema.LocalBookkeepingStoreName,
                        Value: new RevisionRecord(PolicyRevisionKey, expectedRevision + 1))
                ],
                cancellationToken).ConfigureAwait(false);
        }
        catch (JSException exception) when (
            exception.Message.Contains("policy.revision-conflict", StringComparison.Ordinal))
        {
            throw new CapabilityGrantConflictException(
                "The durable capability policy changed before the mutation could commit.",
                exception);
        }
    }

    private static long ReadRevision(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("value", out JsonElement value)
            || !value.TryGetInt64(out long revision)
            || revision < 0)
        {
            throw new InvalidDataException("The persisted policy revision is invalid.");
        }

        return revision;
    }

    private static bool Allows(CapabilityGrant grant, CapabilityResourceCandidate? candidate)
    {
        if (candidate is null)
        {
            return grant.Constraints.Count == 0;
        }

        return candidate switch
        {
            VirtualPathResourceCandidate path => grant.Constraints
                .OfType<VirtualPathCapabilityConstraint>()
                .Any(constraint => constraint.Allows(path.Path)),
            NetworkHostResourceCandidate host => grant.Constraints
                .OfType<NetworkHostCapabilityConstraint>()
                .Any(constraint => StringComparer.Ordinal.Equals(constraint.Host, host.Host)),
            NetworkPortResourceCandidate port => grant.Constraints
                .OfType<NetworkPortCapabilityConstraint>()
                .Any(constraint => port.Port >= constraint.MinimumPort && port.Port <= constraint.MaximumPort),
            _ => false
        };
    }

    private static bool IsBroaderOrEqual(
        IReadOnlyList<CapabilityConstraint> candidate,
        IReadOnlyList<CapabilityConstraint> baseline)
    {
        if (baseline.Count == 0)
        {
            return candidate.Count == 0;
        }

        if (candidate.Count == 0)
        {
            return true;
        }

        return baseline.All(baselineConstraint => candidate.Any(candidateConstraint =>
            candidateConstraint.Kind == baselineConstraint.Kind
            && IsBroaderOrEqual(candidateConstraint, baselineConstraint)));
    }

    private static bool IsBroaderOrEqual(CapabilityConstraint candidate, CapabilityConstraint baseline) =>
        (candidate, baseline) switch
        {
            (VirtualPathCapabilityConstraint current, VirtualPathCapabilityConstraint prior) =>
                current.Allows(prior.Path) && (current.IncludeDescendants || !prior.IncludeDescendants),
            (NetworkPortCapabilityConstraint current, NetworkPortCapabilityConstraint prior) =>
                current.MinimumPort <= prior.MinimumPort && current.MaximumPort >= prior.MaximumPort,
            (NetworkHostCapabilityConstraint current, NetworkHostCapabilityConstraint prior) =>
                StringComparer.Ordinal.Equals(current.Host, prior.Host),
            _ => false
        };

    private sealed record RevisionRecord(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("value")] long Value);

    private sealed record GrantRecord(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("appId")] string AppId,
        [property: JsonPropertyName("userId")] string UserId,
        [property: JsonPropertyName("capability")] string Capability,
        [property: JsonPropertyName("policyRevision")] long PolicyRevision,
        [property: JsonPropertyName("source")] int Source,
        [property: JsonPropertyName("constraints")] IReadOnlyList<ConstraintRecord> Constraints,
        [property: JsonPropertyName("revokedAtUtcMs")] long? RevokedAtUtcMs,
        [property: JsonPropertyName("revokedRevision")] long? RevokedRevision)
    {
        internal static GrantRecord FromDomain(CapabilityGrant grant) => new(
            grant.Id.ToString(),
            grant.AppId,
            grant.UserId,
            grant.Capability,
            grant.PolicyRevision,
            (int)grant.Source,
            grant.Constraints.Select(ConstraintRecord.FromDomain).ToArray(),
            null,
            null);

        internal CapabilityGrant ToDomain() => new(
            CapabilityGrantId.FromGuid(Guid.ParseExact(Id, "N")),
            AppId,
            UserId,
            Capability,
            PolicyRevision,
            Enum.IsDefined((CapabilityGrantSource)Source)
                ? (CapabilityGrantSource)Source
                : throw new InvalidDataException($"Persisted grant source '{Source}' is invalid."),
            Constraints.Select(static constraint => constraint.ToDomain()));

        internal static GrantRecord FromJson(JsonElement element) => new(
            element.GetProperty("id").GetString()!,
            element.GetProperty("appId").GetString()!,
            element.GetProperty("userId").GetString()!,
            element.GetProperty("capability").GetString()!,
            element.GetProperty("policyRevision").GetInt64(),
            element.GetProperty("source").GetInt32(),
            element.GetProperty("constraints").EnumerateArray().Select(ConstraintRecord.FromJson).ToArray(),
            element.TryGetProperty("revokedAtUtcMs", out JsonElement revokedAt)
                && revokedAt.ValueKind != JsonValueKind.Null ? revokedAt.GetInt64() : null,
            element.TryGetProperty("revokedRevision", out JsonElement revokedRevision)
                && revokedRevision.ValueKind != JsonValueKind.Null ? revokedRevision.GetInt64() : null);
    }

    private sealed record ConstraintRecord(
        [property: JsonPropertyName("kind")] int Kind,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("includeDescendants")] bool? IncludeDescendants,
        [property: JsonPropertyName("host")] string? Host,
        [property: JsonPropertyName("minimumPort")] ushort? MinimumPort,
        [property: JsonPropertyName("maximumPort")] ushort? MaximumPort)
    {
        internal static ConstraintRecord FromDomain(CapabilityConstraint constraint) => constraint switch
        {
            VirtualPathCapabilityConstraint path => new(
                (int)path.Kind, path.Path.Value, path.IncludeDescendants, null, null, null),
            NetworkHostCapabilityConstraint host => new(
                (int)host.Kind, null, null, host.Host, null, null),
            NetworkPortCapabilityConstraint port => new(
                (int)port.Kind, null, null, null, port.MinimumPort, port.MaximumPort),
            _ => throw new InvalidDataException($"Unsupported capability constraint '{constraint.Kind}'.")
        };

        internal CapabilityConstraint ToDomain() => (CapabilityConstraintKind)Kind switch
        {
            CapabilityConstraintKind.VirtualPath when Path is not null && IncludeDescendants is not null =>
                new VirtualPathCapabilityConstraint(VirtualPath.Parse(Path), IncludeDescendants.Value),
            CapabilityConstraintKind.NetworkHost when Host is not null =>
                new NetworkHostCapabilityConstraint(Host),
            CapabilityConstraintKind.NetworkPort when MinimumPort is not null && MaximumPort is not null =>
                new NetworkPortCapabilityConstraint(MinimumPort.Value, MaximumPort.Value),
            _ => throw new InvalidDataException($"Persisted capability constraint '{Kind}' is invalid.")
        };

        internal static ConstraintRecord FromJson(JsonElement element) => new(
            element.GetProperty("kind").GetInt32(),
            OptionalString(element, "path"),
            OptionalBoolean(element, "includeDescendants"),
            OptionalString(element, "host"),
            OptionalUInt16(element, "minimumPort"),
            OptionalUInt16(element, "maximumPort"));

        private static string? OptionalString(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind != JsonValueKind.Null
                ? value.GetString()
                : null;

        private static bool? OptionalBoolean(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind != JsonValueKind.Null
                ? value.GetBoolean()
                : null;

        private static ushort? OptionalUInt16(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind != JsonValueKind.Null
                ? value.GetUInt16()
                : null;
    }

    private sealed record AuditRecord(
        [property: JsonPropertyName("timestampUtcMs")] long TimestampUtcMs,
        [property: JsonPropertyName("correlationId")] string CorrelationId,
        [property: JsonPropertyName("actor")] string Actor,
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("outcome")] int Outcome,
        [property: JsonPropertyName("properties")] IReadOnlyDictionary<string, string> Properties)
    {
        internal static AuditRecord Create(
            string action,
            CapabilityGrant grant,
            long revision,
            AppAuthority authority,
            DateTimeOffset timestamp,
            Guid correlationId) => new(
                timestamp.ToUnixTimeMilliseconds(),
                correlationId.ToString("N"),
                authority.ToString(),
                action,
                grant.Id.ToString(),
                0,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["appId"] = grant.AppId,
                    ["userId"] = grant.UserId,
                    ["capability"] = grant.Capability,
                    ["policyRevision"] = revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["source"] = grant.Source.ToString()
                });
    }
}