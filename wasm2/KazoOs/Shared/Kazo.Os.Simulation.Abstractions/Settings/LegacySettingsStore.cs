namespace Kazo.Os.Simulation.Abstractions.Settings;

/// <summary>One record of the retired IndexedDB <c>settings</c> store, read only by the migration.</summary>
/// <param name="Scope">Historical scope (<see cref="SettingsScope"/>).</param>
/// <param name="AppId">Owning app, for app scopes.</param>
/// <param name="UserId">Owning user id, for user scopes.</param>
/// <param name="DocumentId">Document id.</param>
/// <param name="Content">Serialized content.</param>
public sealed record LegacySettingsRecord(SettingsScope Scope, string? AppId, string? UserId, string DocumentId, string Content);

/// <summary>
/// Read-only access to the retired IndexedDB <c>settings</c> store. The store is neither emptied
/// nor deleted and the IndexedDB schema version does not change (ADR 0044 section 9).
/// </summary>
public interface ILegacySettingsStore
{
    /// <summary>Reads every record, or an empty list when the store holds none.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<IReadOnlyList<LegacySettingsRecord>> ReadAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>A store with no records: fresh installs and hosts without IndexedDB.</summary>
public sealed class EmptyLegacySettingsStore : ILegacySettingsStore
{
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<LegacySettingsRecord>> ReadAllAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<LegacySettingsRecord>>([]);
}
