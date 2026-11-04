using Kazo.Os.Server.Data;
using Kazo.Os.Server.Services;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Kazo.Os.Server.Tests;

/// <summary>
/// Serializes tests that configure the server host via process-wide
/// <c>HACKEROS_*</c> environment variables (read once at <see cref="Program"/>
/// startup) — xUnit runs different test classes concurrently by default, and
/// two such tests racing would each observe the other's connection string.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ServerEnvironmentCollection
{
    public const string Name = "Kazo.Os.Server environment-variable tests";
}

/// <summary>
/// Exercises the optional server's real application composition, including the
/// SQLite migration performed during startup and the unauthenticated health endpoint.
/// </summary>
[Collection(ServerEnvironmentCollection.Name)]
public sealed class ServerStartupIntegrationTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"hackeros-server-integration-{Guid.NewGuid():N}.db");
    private readonly string _backupRoot = Path.Combine(
        Path.GetTempPath(),
        $"hackeros-server-backups-{Guid.NewGuid():N}");
    private string? _previousConnectionString;
    private string? _previousBackupRoot;
    private ServerApplicationFactory? _factory;

    public Task InitializeAsync()
    {
        _previousConnectionString = Environment.GetEnvironmentVariable("HACKEROS_ConnectionStrings__HackerOsDb");
        _previousBackupRoot = Environment.GetEnvironmentVariable("HACKEROS_ServerBackup__Root");
        Environment.SetEnvironmentVariable("HACKEROS_ConnectionStrings__HackerOsDb", $"Data Source={_databasePath}");
        Environment.SetEnvironmentVariable("HACKEROS_ServerBackup__Root", _backupRoot);
        _factory = new ServerApplicationFactory();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        Environment.SetEnvironmentVariable("HACKEROS_ConnectionStrings__HackerOsDb", _previousConnectionString);
        Environment.SetEnvironmentVariable("HACKEROS_ServerBackup__Root", _previousBackupRoot);

        // SQLite can retain a pooled handle briefly after the test host disposes.
        // The uniquely named OS-temp database is deliberately left for the OS temp
        // cleanup policy instead of making a successful integration test flaky.
    }

    [Fact]
    public async Task Startup_MigratesConfiguredDatabase_BacksUpRestores_AndReportsHealthy()
    {
        var factory = Assert.IsType<ServerApplicationFactory>(_factory);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");
        var payload = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains("\"status\":\"healthy\"", payload, StringComparison.Ordinal);

        using var protectedResponse = await client.GetAsync("/api/account/data-summary");
        Assert.Equal(HttpStatusCode.Unauthorized, protectedResponse.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<KazoOsServerDbContext>();
        Assert.True(await database.Database.CanConnectAsync());
        Assert.Equal(_databasePath, database.Database.GetDbConnection().DataSource);
        Assert.True(File.Exists(_databasePath));

        var accountId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        database.Accounts.Add(new AccountEntity
        {
            AccountId = accountId,
            Username = "authenticated-owner",
            PasswordHash = "hash",
            Salt = "salt",
            CreatedUtc = DateTimeOffset.UtcNow
        });
        await database.SaveChangesAsync();

        var tokens = factory.Services.GetRequiredService<ITokenService>();
        var (accessToken, _) = await tokens.IssueAccessTokenAsync(accountId, deviceId, CancellationToken.None);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var authenticatedResponse = await client.GetAsync("/api/account/data-summary");
        authenticatedResponse.EnsureSuccessStatusCode();
        Assert.Contains("authenticated-owner", await authenticatedResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var backups = factory.Services.GetRequiredService<IServerDatabaseBackupService>();
        ServerDatabaseBackupResult snapshot = await backups.CreateAsync("integration.db", CancellationToken.None);
        Assert.True(File.Exists(snapshot.Path));
        await Assert.ThrowsAsync<ArgumentException>(() => backups.CreateAsync("../escape.db", CancellationToken.None));

        database.Accounts.Add(new AccountEntity
        {
            AccountId = Guid.NewGuid(),
            Username = "restore-proof",
            PasswordHash = "hash",
            Salt = "salt",
            CreatedUtc = DateTimeOffset.UtcNow
        });
        await database.SaveChangesAsync();
        await backups.RestoreAsync("integration.db", CancellationToken.None);

        await using var restoredScope = factory.Services.CreateAsyncScope();
        var restoredDatabase = restoredScope.ServiceProvider.GetRequiredService<KazoOsServerDbContext>();
        Assert.False(await restoredDatabase.Accounts.AnyAsync(account => account.Username == "restore-proof"));
    }

    private sealed class ServerApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
        }
    }
}
