using System.Text.Json.Serialization;

namespace Kazo.Os.Simulation.Abstractions.Sync;

// =============================================================================
// Grants Domain Sync Payload — ADR 0031
//
// Pull-only: the client never pushes a Grants envelope (ADR 0031 Decision 1). This
// payload is only ever read after a pull. Revocation is a field on the payload, not
// a tombstone — tombstones are blocked server-side for this domain (ADR 0025).
// =============================================================================

/// <summary>
/// Reconstructs one durable <c>CapabilityGrant</c> from a pulled sync record (ADR 0031 Decision 5).
/// </summary>
/// <param name="AppId">Exact app ID the grant applies to.</param>
/// <param name="UserId">Exact user ID the grant applies to.</param>
/// <param name="Capability">Exact known capability identifier.</param>
/// <param name="Source">One of the <c>CapabilityGrantSource</c> enum names.</param>
/// <param name="Constraints">Structured resource constraints, at most one per kind.</param>
/// <param name="IsRevoked">True when the server has revoked this grant.</param>
public sealed record GrantsSyncPayload(
    string AppId,
    string UserId,
    string Capability,
    string Source,
    IReadOnlyList<GrantConstraintPayload> Constraints,
    bool IsRevoked);

/// <summary>
/// Flattened structured resource constraint. Exactly one of the kind-specific field groups is
/// populated, selected by <see cref="Kind"/> (one of the <c>CapabilityConstraintKind</c> enum names).
/// </summary>
public sealed record GrantConstraintPayload(
    string Kind,
    string? PathValue,
    bool? IncludeDescendants,
    string? Host,
    int? MinPort,
    int? MaxPort);

/// <summary>Source-generated JSON context for the Grants sync payload.</summary>
[JsonSerializable(typeof(GrantsSyncPayload))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public sealed partial class GrantsSyncContractsJsonContext : JsonSerializerContext { }
