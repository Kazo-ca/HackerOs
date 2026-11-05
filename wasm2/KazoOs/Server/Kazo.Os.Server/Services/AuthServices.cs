using System.Security.Claims;
using System.Text.Encodings.Web;
using Kazo.Os.Server.Contracts.Proxy;
using Kazo.Os.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kazo.Os.Server.Services;

// =============================================================================
// Bearer Authentication Handler
//
// Access tokens are opaque random strings stored server-side (not JWTs).
// The handler looks up the token hash in the RefreshTokens table to find the
// associated DeviceId and AccountId. This avoids shipping a signing secret
// to the client, and allows instant revocation by marking IsInvalidated = true.
//
// Access token storage: the server maintains an in-memory concurrent dictionary
// of SHA-256(token) → (AccountId, DeviceId, ExpiresUtc) to avoid a DB round-trip
// per request. The dictionary is populated on first access and evicted on expiry.
// =============================================================================

/// <summary>Options for the Kazo OS bearer authentication scheme (no configuration fields).</summary>
public sealed class KazoOsBearerOptions : AuthenticationSchemeOptions { }

/// <summary>
/// Custom bearer token authentication handler.
/// Validates opaque access tokens against the server's token cache.
/// Never decodes a JWT; never trusts client-supplied claims.
/// </summary>
public sealed class KazoOsBearerHandler : AuthenticationHandler<KazoOsBearerOptions>
{
    private readonly ITokenService _tokenService;

    /// <inheritdoc />
    public KazoOsBearerHandler(
        IOptionsMonitor<KazoOsBearerOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ITokenService tokenService)
        : base(options, logger, encoder)
    {
        _tokenService = tokenService;
    }

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Extract the bearer token from the Authorization header. A browser WebSocket cannot set
        // that header, so the relay's stream routes (ADR 0102 §2, §3) also accept it as the
        // access_token query parameter, as SignalR does — and only those routes.
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        string token;
        if (authHeader is not null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = authHeader["Bearer ".Length..].Trim();
        }
        else if (IsStreamRoute(Request) && Request.Query[ProxyStreamRoutes.AccessTokenParameter].ToString() is { Length: > 0 } queryToken)
        {
            token = queryToken;
        }
        else
        {
            return AuthenticateResult.NoResult();
        }

        if (string.IsNullOrEmpty(token))
            return AuthenticateResult.Fail("Empty bearer token.");

        // Validate token against the server's cache/store.
        var result = await _tokenService.ValidateAccessTokenAsync(token, Context.RequestAborted);
        if (!result.IsValid)
            return AuthenticateResult.Fail(result.FailureReason ?? "Invalid or expired token.");

        // Build a claims principal with just the stable IDs the server needs.
        // No role or permission claims are accepted from the client side.
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, result.AccountId.ToString()),
            new Claim("device_id", result.DeviceId.ToString())
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    private static bool IsStreamRoute(HttpRequest request) =>
        request.Path.Equals("/" + ProxyStreamRoutes.TcpPath, StringComparison.OrdinalIgnoreCase)
        || request.Path.Equals("/" + ProxyStreamRoutes.UdpPath, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Result of an access token validation attempt.
/// </summary>
public sealed record TokenValidationResult(
    bool IsValid,
    Guid AccountId,
    Guid DeviceId,
    string? FailureReason);

/// <summary>
/// Manages access token creation, validation, and revocation.
/// </summary>
public interface ITokenService
{
    /// <summary>Issues a new short-lived access token bound to a device.</summary>
    Task<(string AccessToken, DateTimeOffset ExpiresUtc)> IssueAccessTokenAsync(
        Guid accountId, Guid deviceId, CancellationToken ct);

    /// <summary>Validates an access token and returns the associated identity.</summary>
    Task<TokenValidationResult> ValidateAccessTokenAsync(string accessToken, CancellationToken ct);

    /// <summary>Revokes all access tokens for a device (called on logout or device removal).</summary>
    void RevokeDevice(Guid deviceId);
}

/// <summary>
/// In-memory access token cache with automatic expiry eviction.
/// </summary>
public sealed class TokenService : ITokenService
{
    private readonly record struct CacheEntry(Guid AccountId, Guid DeviceId, DateTimeOffset ExpiresUtc);

    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    // Access tokens live for 15 minutes.
    private static readonly TimeSpan AccessTokenTtl = TimeSpan.FromMinutes(15);

    /// <inheritdoc />
    public Task<(string AccessToken, DateTimeOffset ExpiresUtc)> IssueAccessTokenAsync(
        Guid accountId, Guid deviceId, CancellationToken ct)
    {
        var token = GenerateOpaqueToken();
        var hash = HashToken(token);
        var expires = DateTimeOffset.UtcNow.Add(AccessTokenTtl);

        lock (_lock)
        {
            // Evict expired entries opportunistically on each issue.
            EvictExpiredLocked();
            _cache[hash] = new CacheEntry(accountId, deviceId, expires);
        }

        return Task.FromResult((token, expires));
    }

    /// <inheritdoc />
    public Task<TokenValidationResult> ValidateAccessTokenAsync(string accessToken, CancellationToken ct)
    {
        var hash = HashToken(accessToken);
        lock (_lock)
        {
            if (_cache.TryGetValue(hash, out var entry) && entry.ExpiresUtc > DateTimeOffset.UtcNow)
                return Task.FromResult(new TokenValidationResult(true, entry.AccountId, entry.DeviceId, null));
        }

        return Task.FromResult(new TokenValidationResult(false, Guid.Empty, Guid.Empty, "Token not found or expired."));
    }

    /// <inheritdoc />
    public void RevokeDevice(Guid deviceId)
    {
        lock (_lock)
        {
            var keys = _cache.Where(kv => kv.Value.DeviceId == deviceId).Select(kv => kv.Key).ToList();
            foreach (var k in keys) _cache.Remove(k);
        }
    }

    private static string GenerateOpaqueToken()
    {
        var bytes = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string HashToken(string token)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(token);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void EvictExpiredLocked()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = _cache.Where(kv => kv.Value.ExpiresUtc <= now).Select(kv => kv.Key).ToList();
        foreach (var k in expired) _cache.Remove(k);
    }
}

/// <summary>
/// PBKDF2-SHA256 password hashing service.
/// The server never stores or logs plaintext passwords.
/// </summary>
public interface IPasswordHashService
{
    /// <summary>Generates a fresh random salt and hashes the password.</summary>
    (string Hash, string Salt) HashPassword(string password);

    /// <summary>Verifies a plaintext password against a stored hash and salt.</summary>
    bool VerifyPassword(string password, string storedHash, string storedSalt);
}

/// <inheritdoc />
public sealed class Pbkdf2PasswordHashService : IPasswordHashService
{
    private const int Iterations = 310_000; // OWASP 2024 recommendation for PBKDF2-SHA256
    private const int HashLength = 32;
    private const int SaltLength = 16;

    /// <inheritdoc />
    public (string Hash, string Salt) HashPassword(string password)
    {
        var saltBytes = new byte[SaltLength];
        System.Security.Cryptography.RandomNumberGenerator.Fill(saltBytes);
        var hashBytes = DeriveKey(password, saltBytes);
        return (Convert.ToHexString(hashBytes).ToLowerInvariant(),
                Convert.ToHexString(saltBytes).ToLowerInvariant());
    }

    /// <inheritdoc />
    public bool VerifyPassword(string password, string storedHash, string storedSalt)
    {
        var saltBytes = Convert.FromHexString(storedSalt);
        var hashBytes = DeriveKey(password, saltBytes);
        var computed = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(computed),
            System.Text.Encoding.ASCII.GetBytes(storedHash));
    }

    private static byte[] DeriveKey(string password, byte[] salt) =>
        System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, System.Security.Cryptography.HashAlgorithmName.SHA256, HashLength);
}
