namespace Kazo.Os.Simulation.Abstractions.ServerConnection;

/// <summary>
/// The single per-device optional-server connection record (ADR 0028): which server account and
/// device this installation is linked to, and the opaque refresh token used to re-derive short-lived
/// access tokens. Not persisted through the ordinary settings projection — access tokens themselves
/// are never persisted, only re-derived on demand from <see cref="RefreshTokenOpaque"/>.
/// </summary>
public sealed record ServerConnectionState(
    Guid AccountId,
    Guid DeviceId,
    string ServerBaseUrl,
    string DeviceFingerprint,
    string RefreshTokenOpaque,
    DateTimeOffset RefreshTokenExpiresUtc);

/// <summary>
/// Persists the single per-device <see cref="ServerConnectionState"/>. A device is either
/// disconnected (no record) or connected to exactly one server account at a time — connecting again
/// replaces the prior record rather than adding a second one.
/// </summary>
public interface IServerConnectionRepository
{
    /// <summary>Gets the current connection state, or <see langword="null"/> if this device is not connected.</summary>
    ValueTask<ServerConnectionState?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists the connection state, replacing any prior record.</summary>
    ValueTask SetAsync(ServerConnectionState state, CancellationToken cancellationToken = default);

    /// <summary>Clears the connection state so this device is disconnected.</summary>
    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}
