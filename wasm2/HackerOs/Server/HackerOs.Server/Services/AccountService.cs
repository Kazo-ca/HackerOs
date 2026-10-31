using HackerOs.Server.Contracts.Identity;
using HackerOs.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace HackerOs.Server.Services;

/// <summary>
/// Account creation, login, device management, and refresh token rotation.
/// </summary>
public interface IAccountService
{
    Task<CreateAccountResponse> CreateAccountAsync(CreateAccountRequest request, CancellationToken ct);
    Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct);
    Task<RefreshTokenResponse> RefreshAsync(RefreshTokenRequest request, CancellationToken ct);
    Task<RegisterDeviceResponse> RegisterDeviceAsync(Guid accountId, RegisterDeviceRequest request, CancellationToken ct);
    Task<IReadOnlyList<DeviceSummary>> GetDevicesAsync(Guid accountId, string callerFingerprint, CancellationToken ct);
    Task RevokeDeviceAsync(Guid accountId, Guid deviceId, CancellationToken ct);

    /// <summary>
    /// Creates <paramref name="username"/> for pairing (ADR 0191 §6) when it does not exist yet, with a
    /// random password nobody knows: its devices join by pairing code. Returns whether it was created.
    /// </summary>
    Task<bool> EnsurePairingAccountAsync(string username, CancellationToken ct);

    /// <summary>
    /// Registers a new device on <paramref name="username"/> after its pairing code was redeemed and
    /// logs it in, as <see cref="LoginAsync"/> does after a password check.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The account no longer exists.</exception>
    Task<LoginResponse> PairDeviceAsync(string username, PairDeviceRequest request, CancellationToken ct);
}

/// <inheritdoc />
public sealed class AccountService : IAccountService
{
    private readonly HackerOsServerDbContext _db;
    private readonly IPasswordHashService _passwordHashService;
    private readonly ITokenService _tokenService;
    private readonly IAuditService _audit;

    // Refresh tokens live for 30 days.
    private static readonly TimeSpan RefreshTokenTtl = TimeSpan.FromDays(30);

    /// <inheritdoc />
    public AccountService(
        HackerOsServerDbContext db,
        IPasswordHashService passwordHashService,
        ITokenService tokenService,
        IAuditService audit)
    {
        _db = db;
        _passwordHashService = passwordHashService;
        _tokenService = tokenService;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task<CreateAccountResponse> CreateAccountAsync(CreateAccountRequest request, CancellationToken ct)
    {
        // Validate username uniqueness.
        if (await _db.Accounts.AnyAsync(a => a.Username == request.Username && !a.IsDeleted, ct))
            throw new InvalidOperationException("USERNAME_TAKEN");

        var accountId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // The client submits a pre-computed PBKDF2 hash. The server applies
        // an additional server-side PBKDF2 round to prevent hash-as-password attacks.
        var (serverHash, serverSalt) = _passwordHashService.HashPassword(request.PasswordHash);

        var account = new AccountEntity
        {
            AccountId = accountId,
            Username = request.Username,
            PasswordHash = serverHash,
            Salt = serverSalt,
            CreatedUtc = now
        };
        _db.Accounts.Add(account);

        var device = new DeviceEntity
        {
            DeviceId = deviceId,
            AccountId = accountId,
            DeviceName = request.DeviceName,
            DeviceFingerprint = request.DeviceFingerprint,
            RegisteredUtc = now
        };
        _db.Devices.Add(device);

        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync(accountId, deviceId, "ACCOUNT_CREATED", null, ct);

        return new CreateAccountResponse(accountId, request.Username, now, deviceId);
    }

    /// <inheritdoc />
    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var account = await _db.Accounts
            .FirstOrDefaultAsync(a => a.Username == request.Username && !a.IsDeleted, ct)
            ?? throw new UnauthorizedAccessException("INVALID_CREDENTIALS");

        // Server-side PBKDF2 round verification.
        if (!_passwordHashService.VerifyPassword(request.PasswordHash, account.PasswordHash, account.Salt))
            throw new UnauthorizedAccessException("INVALID_CREDENTIALS");

        var device = await _db.Devices
            .FirstOrDefaultAsync(d => d.AccountId == account.AccountId
                                   && d.DeviceFingerprint == request.DeviceFingerprint, ct);
        if (device is { IsRevoked: true })
            throw new UnauthorizedAccessException("DEVICE_REVOKED");

        if (device is null)
        {
            // ADR 0137 §4: a new browser (another device, or the OS a server served, ADR 0134) logs
            // into an existing account. The password was just verified, which is what registering a
            // device through POST /api/devices proves with an access token, so the login registers it.
            device = new DeviceEntity
            {
                DeviceId = Guid.NewGuid(),
                AccountId = account.AccountId,
                DeviceName = "HackerOS",
                DeviceFingerprint = request.DeviceFingerprint,
                RegisteredUtc = DateTimeOffset.UtcNow
            };
            _db.Devices.Add(device);
            await _audit.RecordAsync(account.AccountId, device.DeviceId, "DEVICE_REGISTERED_AT_LOGIN", null, ct);
        }

        device.LastSeenUtc = DateTimeOffset.UtcNow;

        // Issue access token (in-memory) and refresh token (persisted, hashed).
        var (accessToken, accessExpires) = await _tokenService.IssueAccessTokenAsync(account.AccountId, device.DeviceId, ct);
        var (refreshToken, refreshExpires) = await IssueRefreshTokenAsync(device.DeviceId, ct);

        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync(account.AccountId, device.DeviceId, "LOGIN", null, ct);

        return new LoginResponse(
            account.AccountId, device.DeviceId,
            accessToken, refreshToken,
            accessExpires, refreshExpires);
    }

    /// <inheritdoc />
    public async Task<RefreshTokenResponse> RefreshAsync(RefreshTokenRequest request, CancellationToken ct)
    {
        var tokenHash = HashRefreshToken(request.RefreshToken);

        // Find the stored token and the device it belongs to.
        var stored = await _db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash
                                   && !t.IsInvalidated
                                   && t.ExpiresUtc > DateTimeOffset.UtcNow, ct)
            ?? throw new UnauthorizedAccessException("INVALID_REFRESH_TOKEN");

        var device = await _db.Devices
            .FirstOrDefaultAsync(d => d.DeviceId == stored.DeviceId && !d.IsRevoked, ct)
            ?? throw new UnauthorizedAccessException("DEVICE_REVOKED");

        // Validate device fingerprint matches.
        if (device.DeviceFingerprint != request.DeviceFingerprint)
            throw new UnauthorizedAccessException("DEVICE_MISMATCH");

        // Rotate: invalidate old, issue new.
        stored.IsInvalidated = true;
        var (newRefresh, newRefreshExpires) = await IssueRefreshTokenAsync(stored.DeviceId, ct);
        var (newAccess, newAccessExpires) = await _tokenService.IssueAccessTokenAsync(
            device.AccountId, stored.DeviceId, ct);

        device.LastSeenUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new RefreshTokenResponse(newAccess, newAccessExpires, newRefresh, newRefreshExpires);
    }

    /// <inheritdoc />
    public async Task<RegisterDeviceResponse> RegisterDeviceAsync(
        Guid accountId, RegisterDeviceRequest request, CancellationToken ct)
    {
        var deviceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        _db.Devices.Add(new DeviceEntity
        {
            DeviceId = deviceId,
            AccountId = accountId,
            DeviceName = request.DeviceName,
            DeviceFingerprint = request.DeviceFingerprint,
            RegisteredUtc = now
        });
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync(accountId, deviceId, "DEVICE_REGISTERED", null, ct);
        return new RegisterDeviceResponse(deviceId, request.DeviceName, now);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceSummary>> GetDevicesAsync(
        Guid accountId, string callerFingerprint, CancellationToken ct)
    {
        // Ordered here, not in SQL: SQLite cannot sort a DateTimeOffset column (NotSupportedException),
        // and an account has a handful of devices.
        var devices = await _db.Devices
            .Where(d => d.AccountId == accountId && !d.IsRevoked)
            .ToListAsync(ct);

        return devices
            .OrderBy(d => d.RegisteredUtc)
            .Select(d => new DeviceSummary(
                d.DeviceId, d.DeviceName, d.RegisteredUtc, d.LastSeenUtc,
                d.DeviceFingerprint == callerFingerprint))
            .ToList();
    }

    /// <inheritdoc />
    public async Task RevokeDeviceAsync(Guid accountId, Guid deviceId, CancellationToken ct)
    {
        var device = await _db.Devices
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.AccountId == accountId, ct)
            ?? throw new KeyNotFoundException("DEVICE_NOT_FOUND");

        device.IsRevoked = true;
        _tokenService.RevokeDevice(deviceId);

        // Invalidate all refresh tokens for this device.
        await _db.RefreshTokens
            .Where(t => t.DeviceId == deviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsInvalidated, true), ct);

        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync(accountId, deviceId, "DEVICE_REVOKED", null, ct);
    }

    /// <inheritdoc />
    public async Task<bool> EnsurePairingAccountAsync(string username, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        if (await _db.Accounts.AnyAsync(a => a.Username == username && !a.IsDeleted, ct))
        {
            return false;
        }

        // The stored hash is of 32 random bytes: no password can match it. The account can still be
        // given a password later by an administrator; until then only pairing codes open it.
        string unusablePassword = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var (serverHash, serverSalt) = _passwordHashService.HashPassword(unusablePassword);
        var account = new AccountEntity
        {
            AccountId = Guid.NewGuid(),
            Username = username,
            PasswordHash = serverHash,
            Salt = serverSalt,
            CreatedUtc = DateTimeOffset.UtcNow
        };
        _db.Accounts.Add(account);
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync(account.AccountId, null, "ACCOUNT_CREATED_FOR_PAIRING", null, ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<LoginResponse> PairDeviceAsync(string username, PairDeviceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var account = await _db.Accounts
            .FirstOrDefaultAsync(a => a.Username == username && !a.IsDeleted, ct)
            ?? throw new UnauthorizedAccessException("INVALID_PAIRING_CODE");

        var now = DateTimeOffset.UtcNow;
        var device = new DeviceEntity
        {
            DeviceId = Guid.NewGuid(),
            AccountId = account.AccountId,
            DeviceName = string.IsNullOrWhiteSpace(request.DeviceName) ? "HackerOS" : request.DeviceName[..Math.Min(request.DeviceName.Length, 128)],
            DeviceFingerprint = request.DeviceFingerprint,
            RegisteredUtc = now,
            LastSeenUtc = now
        };
        _db.Devices.Add(device);

        var (accessToken, accessExpires) = await _tokenService.IssueAccessTokenAsync(account.AccountId, device.DeviceId, ct);
        var (refreshToken, refreshExpires) = await IssueRefreshTokenAsync(device.DeviceId, ct);
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync(account.AccountId, device.DeviceId, "DEVICE_PAIRED", null, ct);

        return new LoginResponse(account.AccountId, device.DeviceId, accessToken, refreshToken, accessExpires, refreshExpires);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<(string Token, DateTimeOffset Expires)> IssueRefreshTokenAsync(Guid deviceId, CancellationToken ct)
    {
        var token = GenerateRefreshToken();
        var hash = HashRefreshToken(token);
        var expires = DateTimeOffset.UtcNow.Add(RefreshTokenTtl);

        _db.RefreshTokens.Add(new RefreshTokenEntity
        {
            DeviceId = deviceId,
            TokenHash = hash,
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = expires
        });
        await _db.SaveChangesAsync(ct);
        return (token, expires);
    }

    private static string GenerateRefreshToken()
    {
        var bytes = new byte[64];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string HashRefreshToken(string token)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(token);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
