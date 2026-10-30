using System.Text.Json.Serialization;

namespace HackerOs.Server.Contracts.Identity;

// =============================================================================
// ADR 0024 — Server Identity: Device Registration and Synchronized Sessions
//
// Decision: Local-only users remain fully supported without any server account.
// When a user opts in to sync, they create a server account and register their
// device. The server issues a short-lived access token and a long-lived refresh
// token per device. No client-side claims are trusted; the server re-validates
// the acting user on every request from the stored device registration.
//
// The client never sends passwords in API payloads beyond the login request.
// All tokens are opaque strings; the server treats them as bearer secrets, not
// decoded JWTs on the client.
// =============================================================================

/// <summary>
/// Initiates a new server account from a local user's credentials.
/// The server never stores the plaintext password.
/// </summary>
/// <param name="Username">Chosen server account username.</param>
/// <param name="PasswordHash">Client-computed PBKDF2-SHA256 password hash (hex, 64 chars).</param>
/// <param name="Salt">Client-generated PBKDF2 salt (hex, 32 chars).</param>
/// <param name="DeviceName">Human-readable name for the first registered device (max 128 chars).</param>
/// <param name="DeviceFingerprint">Opaque stable device fingerprint (browser agent string hash or UUID).</param>
public sealed record CreateAccountRequest(
    string Username,
    string PasswordHash,
    string Salt,
    string DeviceName,
    string DeviceFingerprint);

/// <summary>
/// Server-side account record returned after successful creation.
/// </summary>
/// <param name="AccountId">Stable UUID assigned by the server.</param>
/// <param name="Username">Confirmed username.</param>
/// <param name="CreatedUtc">Account creation timestamp.</param>
/// <param name="DeviceId">Stable UUID for the newly registered device.</param>
public sealed record CreateAccountResponse(
    Guid AccountId,
    string Username,
    DateTimeOffset CreatedUtc,
    Guid DeviceId);

/// <summary>
/// Login request for an existing account.
/// </summary>
/// <param name="Username">Server account username.</param>
/// <param name="PasswordHash">Client-computed PBKDF2 hash matching stored credentials.</param>
/// <param name="DeviceFingerprint">Identifies which registered device is logging in.</param>
public sealed record LoginRequest(
    string Username,
    string PasswordHash,
    string DeviceFingerprint);

/// <summary>
/// Successful authentication response.
/// </summary>
/// <param name="AccountId">Stable server account UUID.</param>
/// <param name="DeviceId">Confirmed device UUID.</param>
/// <param name="AccessToken">Short-lived bearer token (15-minute TTL). Never store in localStorage.</param>
/// <param name="RefreshToken">Long-lived opaque rotation token (30-day TTL). Store in IndexedDB only.</param>
/// <param name="AccessTokenExpiresUtc">Expiry timestamp for the access token.</param>
/// <param name="RefreshTokenExpiresUtc">Expiry timestamp for the refresh token.</param>
public sealed record LoginResponse(
    Guid AccountId,
    Guid DeviceId,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresUtc,
    DateTimeOffset RefreshTokenExpiresUtc);

/// <summary>
/// Access token refresh request.
/// </summary>
/// <param name="RefreshToken">Current valid refresh token.</param>
/// <param name="DeviceFingerprint">Must match the device that issued the refresh token.</param>
public sealed record RefreshTokenRequest(
    string RefreshToken,
    string DeviceFingerprint);

/// <summary>
/// New access token issued after refresh.
/// </summary>
/// <param name="AccessToken">New short-lived bearer token.</param>
/// <param name="AccessTokenExpiresUtc">Expiry of the new access token.</param>
/// <param name="RefreshToken">Rotated refresh token; the prior token is immediately invalidated.</param>
/// <param name="RefreshTokenExpiresUtc">Expiry of the new refresh token.</param>
public sealed record RefreshTokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresUtc,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresUtc);

/// <summary>
/// Registers a new device for an already-authenticated account.
/// Must be called with a valid access token.
/// </summary>
/// <param name="DeviceName">Human-readable device name (max 128 chars).</param>
/// <param name="DeviceFingerprint">Stable opaque fingerprint for the new device.</param>
public sealed record RegisterDeviceRequest(
    string DeviceName,
    string DeviceFingerprint);

/// <summary>
/// Newly registered device record.
/// </summary>
/// <param name="DeviceId">Stable UUID assigned by the server.</param>
/// <param name="DeviceName">Confirmed device name.</param>
/// <param name="RegisteredUtc">Registration timestamp.</param>
public sealed record RegisterDeviceResponse(
    Guid DeviceId,
    string DeviceName,
    DateTimeOffset RegisteredUtc);

/// <summary>
/// Summary of a registered device for account management.
/// </summary>
/// <param name="DeviceId">Stable UUID.</param>
/// <param name="DeviceName">Human-readable name.</param>
/// <param name="RegisteredUtc">Registration timestamp.</param>
/// <param name="LastSeenUtc">Most recent authenticated access. Null if never used after registration.</param>
/// <param name="IsCurrentDevice">True when this device matches the caller's fingerprint.</param>
public sealed record DeviceSummary(
    Guid DeviceId,
    string DeviceName,
    DateTimeOffset RegisteredUtc,
    DateTimeOffset? LastSeenUtc,
    bool IsCurrentDevice);

/// <summary>
/// Links a new device to a server account with a one-time pairing code (ADR 0191 §6), instead of
/// the account's password. The code is printed by <c>HackerOs.Server pair &lt;account&gt;</c> on the
/// machine that runs the server; it is valid once, for a few minutes.
/// </summary>
/// <param name="Code">The pairing code, as printed (case and dashes are ignored).</param>
/// <param name="DeviceName">Human-readable name for the new device (max 128 chars).</param>
/// <param name="DeviceFingerprint">Opaque stable fingerprint of the new device.</param>
public sealed record PairDeviceRequest(
    string Code,
    string DeviceName,
    string DeviceFingerprint);

/// <summary>
/// Source-generated JSON context for all identity contracts.
/// </summary>
[JsonSerializable(typeof(CreateAccountRequest))]
[JsonSerializable(typeof(CreateAccountResponse))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(RefreshTokenRequest))]
[JsonSerializable(typeof(RefreshTokenResponse))]
[JsonSerializable(typeof(RegisterDeviceRequest))]
[JsonSerializable(typeof(RegisterDeviceResponse))]
[JsonSerializable(typeof(PairDeviceRequest))]
[JsonSerializable(typeof(DeviceSummary))]
[JsonSerializable(typeof(IReadOnlyList<DeviceSummary>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public sealed partial class IdentityContractsJsonContext : JsonSerializerContext { }
