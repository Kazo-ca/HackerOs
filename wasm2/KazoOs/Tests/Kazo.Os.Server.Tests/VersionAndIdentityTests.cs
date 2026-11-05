using Kazo.Os.Server.Contracts.Versioning;
using Kazo.Os.Server.Contracts.Identity;
using Kazo.Os.Server.Data;
using Kazo.Os.Server.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kazo.Os.Server.Tests;

// =============================================================================
// Versioning Contract Tests — P5-SRV-001
// =============================================================================

public sealed class VersioningContractTests
{
    [Fact]
    public void ApiVersionResponse_SerializesAndDeserializes()
    {
        var response = new ApiVersionResponse(
            "1.0.0", "1.0",
            [new ApiVersionEntry("1.0", "current", null, 2, 99)],
            2);

        var json = System.Text.Json.JsonSerializer.Serialize(
            response, VersioningContractsJsonContext.Default.ApiVersionResponse);

        Assert.Contains("\"serverVersion\":\"1.0.0\"", json);
        Assert.Contains("\"currentApiVersion\":\"1.0\"", json);

        var deserialized = System.Text.Json.JsonSerializer.Deserialize(
            json, VersioningContractsJsonContext.Default.ApiVersionResponse);

        Assert.Equal("1.0.0", deserialized!.ServerVersion);
        Assert.Equal("1.0", deserialized.CurrentApiVersion);
        Assert.Single(deserialized.SupportedVersions);
        Assert.Equal("current", deserialized.SupportedVersions[0].Status);
    }

    [Fact]
    public void CompatibilityCheck_SchemaBelow_IsIncompatible()
    {
        var response = new CompatibilityCheckResponse(false, "Schema too old.", true, false);
        Assert.False(response.Compatible);
        Assert.True(response.UpgradeRequired);
    }

    [Fact]
    public void CompatibilityCheck_SunsetVersion_IsIncompatible()
    {
        var response = new CompatibilityCheckResponse(false, "Version sunset.", false, true);
        Assert.False(response.Compatible);
        Assert.True(response.VersionSunset);
    }
}

// =============================================================================
// Identity Contract Tests — P5-SRV-002
// =============================================================================

public sealed class IdentityContractTests
{
    [Fact]
    public void LoginResponse_SerializesAndDeserializes()
    {
        var accountId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var response = new LoginResponse(
            accountId, deviceId, "access-token", "refresh-token",
            DateTimeOffset.UtcNow.AddMinutes(15),
            DateTimeOffset.UtcNow.AddDays(30));

        var json = System.Text.Json.JsonSerializer.Serialize(
            response, IdentityContractsJsonContext.Default.LoginResponse);

        Assert.Contains("\"accessToken\":\"access-token\"", json);
        Assert.Contains("\"refreshToken\":\"refresh-token\"", json);

        var deserialized = System.Text.Json.JsonSerializer.Deserialize(
            json, IdentityContractsJsonContext.Default.LoginResponse);

        Assert.Equal(accountId, deserialized!.AccountId);
        Assert.Equal(deviceId, deserialized.DeviceId);
    }

    [Fact]
    public void DeviceSummary_IsCurrentDevice_FlagSerializes()
    {
        var summary = new DeviceSummary(
            Guid.NewGuid(), "My PC", DateTimeOffset.UtcNow, null, true);

        var json = System.Text.Json.JsonSerializer.Serialize(
            summary, IdentityContractsJsonContext.Default.DeviceSummary);

        Assert.Contains("\"isCurrentDevice\":true", json);
    }
}

// =============================================================================
// Account Service Unit Tests — P5-SRV-002
// =============================================================================

public sealed class AccountServiceTests : IDisposable
{
    private readonly KazoOsServerDbContext _db;
    private readonly AccountService _svc;
    private readonly TokenService _tokens;

    public AccountServiceTests()
    {
        var options = new DbContextOptionsBuilder<KazoOsServerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new KazoOsServerDbContext(options);
        _tokens = new TokenService();
        var passwords = new Pbkdf2PasswordHashService();
        var audit = new AuditService(_db);
        _svc = new AccountService(_db, passwords, _tokens, audit);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CreateAccount_Succeeds_WithUniqueUsername()
    {
        var request = new CreateAccountRequest(
            "alice", "hash123", "salt456", "Alice's PC", "fp-001");

        var response = await _svc.CreateAccountAsync(request, CancellationToken.None);

        Assert.Equal("alice", response.Username);
        Assert.NotEqual(Guid.Empty, response.AccountId);
        Assert.NotEqual(Guid.Empty, response.DeviceId);
    }

    [Fact]
    public async Task CreateAccount_DuplicateUsername_Throws()
    {
        var request = new CreateAccountRequest("bob", "h", "s", "Bob's Laptop", "fp-002");
        await _svc.CreateAccountAsync(request, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.CreateAccountAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task TokenService_ValidToken_ValidatesSuccessfully()
    {
        var accountId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        var (token, _) = await _tokens.IssueAccessTokenAsync(accountId, deviceId, CancellationToken.None);
        var result = await _tokens.ValidateAccessTokenAsync(token, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(accountId, result.AccountId);
        Assert.Equal(deviceId, result.DeviceId);
    }

    [Fact]
    public async Task TokenService_RevokedDevice_FailsValidation()
    {
        var accountId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        var (token, _) = await _tokens.IssueAccessTokenAsync(accountId, deviceId, CancellationToken.None);
        _tokens.RevokeDevice(deviceId);

        var result = await _tokens.ValidateAccessTokenAsync(token, CancellationToken.None);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void PasswordHashService_RoundTrip_Verifies()
    {
        var svc = new Pbkdf2PasswordHashService();
        var (hash, salt) = svc.HashPassword("secret-password");
        Assert.True(svc.VerifyPassword("secret-password", hash, salt));
        Assert.False(svc.VerifyPassword("wrong-password", hash, salt));
    }
}
