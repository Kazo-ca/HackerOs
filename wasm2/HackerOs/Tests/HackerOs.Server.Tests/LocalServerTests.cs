using System.Net;
using System.Net.Http.Json;
using HackerOs.Server.Contracts.Identity;
using HackerOs.Server.LocalServer;
using HackerOs.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HackerOs.Server.Tests;

/// <summary>
/// One-time pairing codes of the installable server (ADR 0191 §6): single use, expiry, the typed
/// form, and the limit on wrong guesses.
/// </summary>
public sealed class PairingCodeStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hackeros-pairing-{Guid.NewGuid():N}");
    private readonly ManualClock _time = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_code_opens_its_account_once()
    {
        PairingCodeStore store = new(_root, _time);

        string code = store.Create("admin");

        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", code);
        Assert.Equal("admin", store.Redeem(code));
        Assert.Null(store.Redeem(code));
    }

    [Fact]
    public void The_code_is_never_stored_in_clear()
    {
        PairingCodeStore store = new(_root, _time);

        string code = store.Create("admin").Replace("-", string.Empty, StringComparison.Ordinal);

        string file = Assert.Single(Directory.GetFiles(_root));
        Assert.DoesNotContain(code, file, StringComparison.Ordinal);
        Assert.DoesNotContain(code, File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void A_code_typed_in_lower_case_with_spaces_works()
    {
        PairingCodeStore store = new(_root, _time);
        string code = store.Create("admin");

        string typed = " " + code.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal) + " ";

        Assert.Equal("admin", store.Redeem(typed));
    }

    [Fact]
    public void A_code_expires_after_ten_minutes()
    {
        PairingCodeStore store = new(_root, _time);
        string code = store.Create("admin");

        _time.Advance(PairingCodeStore.Lifetime + TimeSpan.FromSeconds(1));

        Assert.Null(store.Redeem(code));
    }

    [Fact]
    public void Too_many_wrong_codes_in_a_minute_are_refused_until_the_minute_ends()
    {
        PairingCodeStore store = new(_root, _time);
        string code = store.Create("admin");
        for (int attempt = 0; attempt < PairingCodeStore.MaxFailedAttemptsPerMinute; attempt++)
        {
            Assert.Null(store.Redeem("AAAA-AAAA"));
        }

        // Even the right code is refused while the limit holds; it still works afterwards.
        Assert.Throws<PairingRateLimitedException>(() => store.Redeem(code));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("admin", store.Redeem(code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABCD-EFG")]
    [InlineData("ABCD-EFGHJ")]
    [InlineData("ABCD-EFG0")]
    public void A_malformed_code_is_not_a_code(string? typed) =>
        Assert.Null(PairingCodeStore.Normalize(typed));

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delay) => _now += delay;
    }
}

/// <summary>The command line of the installable server (ADR 0191 §6).</summary>
public sealed class LocalServerCommandTests
{
    [Fact]
    public void No_command_starts_the_server_with_every_argument()
    {
        string[] args = ["--urls=http://localhost:5888"];

        Assert.Null(LocalServerCommand.Parse(args));
        Assert.Equal(args, LocalServerCommand.HostArguments(args));
    }

    [Fact]
    public void Pair_takes_the_account_and_leaves_the_settings_to_the_host()
    {
        string[] args = ["pair", "admin", "--environment=Production"];

        LocalServerCommand command = Assert.IsType<LocalServerCommand>(LocalServerCommand.Parse(args));

        Assert.Equal("pair", command.Name);
        Assert.Equal("admin", command.Account);
        Assert.Equal(["--environment=Production"], LocalServerCommand.HostArguments(args));
    }

    [Fact]
    public async Task Pair_without_an_account_prints_the_usage_and_fails()
    {
        LocalServerCommand command = LocalServerCommand.Parse(["pair"])!;
        using StringWriter output = new();

        int exitCode = await command.RunAsync(new ServiceCollection().BuildServiceProvider(), output);

        Assert.Equal(2, exitCode);
        Assert.Contains("HackerOs.Server pair <account>", output.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>
/// The installable server end to end in-process (ADR 0191 §3, §6): the <c>pair</c> command creates
/// the account and a code, <c>POST /api/auth/pair</c> exchanges it once for device tokens, unknown
/// <c>Host</c> headers are refused, and the local certificate authority is served when configured.
/// </summary>
[Collection(ServerEnvironmentCollection.Name)]
public sealed class LocalServerIntegrationTests : IAsyncLifetime
{
    private static readonly string[] Variables =
    [
        "HACKEROS_ConnectionStrings__HackerOsDb",
        "HACKEROS_HackerOs__Pairing__Root",
        "HACKEROS_AllowedHosts",
        "HACKEROS_HackerOs__LocalServer__CaCertificatePath",
    ];

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"hackeros-local-server-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _previous = [];
    private WebApplicationFactory<Program>? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        string ca = Path.Combine(_folder, "hackeros-local-ca.crt");
        File.WriteAllText(ca, "-----BEGIN CERTIFICATE-----\nTEST\n-----END CERTIFICATE-----\n");
        foreach (string name in Variables)
        {
            _previous[name] = Environment.GetEnvironmentVariable(name);
        }

        Environment.SetEnvironmentVariable(Variables[0], $"Data Source={Path.Combine(_folder, "hackeros.db")}");
        Environment.SetEnvironmentVariable(Variables[1], Path.Combine(_folder, "pairing"));
        Environment.SetEnvironmentVariable(Variables[2], "localhost;127.0.0.1;[::1]");
        Environment.SetEnvironmentVariable(Variables[3], ca);
        _factory = new TestingFactory();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        foreach ((string name, string? value) in _previous)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public async Task The_pair_command_creates_the_account_and_its_code_opens_one_device()
    {
        WebApplicationFactory<Program> factory = _factory!;
        using HttpClient client = factory.CreateClient();
        using StringWriter output = new();

        int exitCode = await LocalServerCommand.Parse(["pair", "admin"])!.RunAsync(factory.Services, output);
        string code = output.ToString().Split("Pairing code for 'admin': ")[1][..9];

        Assert.Equal(0, exitCode);
        Assert.Contains("Account 'admin' created.", output.ToString(), StringComparison.Ordinal);

        using HttpResponseMessage paired = await client.PostAsJsonAsync(
            "/api/auth/pair", new PairDeviceRequest(code, "Laptop", "fp-laptop"), IdentityContractsJsonContext.Default.PairDeviceRequest);
        paired.EnsureSuccessStatusCode();
        LoginResponse? login = await paired.Content.ReadFromJsonAsync(IdentityContractsJsonContext.Default.LoginResponse);
        Assert.False(string.IsNullOrEmpty(login?.RefreshToken));

        // The device uses its tokens like after a password login.
        using HttpRequestMessage devices = new(HttpMethod.Get, "/api/devices");
        devices.Headers.Authorization = new("Bearer", login!.AccessToken);
        using HttpResponseMessage listed = await client.SendAsync(devices);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        using HttpResponseMessage again = await client.PostAsJsonAsync(
            "/api/auth/pair", new PairDeviceRequest(code, "Laptop", "fp-other"), IdentityContractsJsonContext.Default.PairDeviceRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
    }

    [Fact]
    public async Task A_second_pair_command_reuses_the_account()
    {
        WebApplicationFactory<Program> factory = _factory!;
        _ = factory.Server;
        using StringWriter first = new();
        using StringWriter second = new();

        await LocalServerCommand.Parse(["pair", "benoit"])!.RunAsync(factory.Services, first);
        await LocalServerCommand.Parse(["pair", "benoit"])!.RunAsync(factory.Services, second);

        Assert.Contains("created", first.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("created", second.ToString(), StringComparison.Ordinal);
        using IServiceScope scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<IAccountService>().EnsurePairingAccountAsync("benoit", CancellationToken.None));
    }

    [Fact]
    public async Task An_unknown_Host_header_is_refused()
    {
        using HttpClient client = _factory!.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/version");
        request.Headers.Host = "attacker.example";

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_local_certificate_authority_is_served_when_configured()
    {
        using HttpClient client = _factory!.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(LocalServerEndpoints.LocalCaPath);

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/x-x509-ca-cert", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("BEGIN CERTIFICATE", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_GitHub_Pages_origin_may_call_the_server()
    {
        using HttpClient client = _factory!.CreateClient();
        using HttpRequestMessage preflight = new(HttpMethod.Options, "/api/auth/pair");
        preflight.Headers.Add("Origin", "https://kazo-ca.github.io");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");

        using HttpResponseMessage response = await client.SendAsync(preflight);

        Assert.Equal("https://kazo-ca.github.io", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    private sealed class TestingFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");
    }
}
