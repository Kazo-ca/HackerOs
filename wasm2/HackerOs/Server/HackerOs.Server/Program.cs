using Microsoft.Extensions.DependencyInjection.Extensions;
using HackerOs.Server;
using HackerOs.Server.Data;
using HackerOs.Server.Endpoints;
using HackerOs.Server.LocalServer;
using HackerOs.Server.Services;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

// =============================================================================
// HackerOs Optional Server
//
// This process is entirely optional. The browser PWA continues to function
// fully when this server is not reachable. The server provides:
//   0. The OS itself (ADR 0130): the published files of OS/HackerOs.Ecosystem, served as they
//      are. The OS runs in the browser in WebAssembly, exactly as the standalone PWA does; it
//      reaches the endpoints below over HTTP and WebSocket like any other client.
//   1. Sync — record-level versioned push/pull with explicit conflict resolution.
//   2. Identity — account registration, device management, token refresh.
//   3. Network relay — server-side validated HTTP requests (with their bodies), a single-port
//      TCP probe, and TCP/UDP streams over WebSockets, for apps the server grants
//      network.real.access to (ADR 0035, ADR 0102). Its policy and quotas are the
//      HackerOs:Proxy section of appsettings.json; risky settings are logged at startup.
//
// Security: The server never trusts any client-side claims. It validates
// every request against its own stored device policy and capability grants.
// =============================================================================

// ADR 0191: `HackerOs.Server pair <account>` runs a command against this server's configuration
// instead of starting it; the host only sees the arguments that follow the command.
LocalServerCommand? command = LocalServerCommand.Parse(args);

// ADR 0191 §4: an installed server runs as a Windows service, a systemd unit or a launchd agent.
// A Windows service starts in System32: its content root (appsettings.json, wwwroot, relative data
// paths) is the program's folder instead. systemd and launchd set the working directory themselves.
bool isWindowsService = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();
if (isWindowsService)
{
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = LocalServerCommand.HostArguments(args),
    ContentRootPath = isWindowsService ? AppContext.BaseDirectory : null
});
builder.Services.AddWindowsService(options => options.ServiceName = "HackerOS Server");
builder.Services.AddSystemd();

// ── Configuration ─────────────────────────────────────────────────────────────
// WebApplication.CreateBuilder already loads appsettings.json, the environment
// variant, standard environment variables, and command-line arguments. Add only
// the documented HACKEROS_ override layer so deployments can use scoped variables
// without re-adding JSON sources after host/test configuration.
// ADR 0191 §3: before it, hackeros-server.json, written by the install scripts next to the program
// (listening address, AllowedHosts, origins, data folders, certificate); absent elsewhere.
builder.Configuration.AddJsonFile(
    Path.Combine(builder.Environment.ContentRootPath, "hackeros-server.json"), optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables(prefix: "HACKEROS_");

// ── Database ──────────────────────────────────────────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("HackerOsDb")
    ?? "Data Source=hackeros.db";
builder.Services.AddDbContext<HackerOsServerDbContext>(options =>
    options.UseSqlite(connectionString));

// ── Core server services ──────────────────────────────────────────────────────
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddSingleton<IPasswordHashService, Pbkdf2PasswordHashService>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<ISyncService, SyncService>();
// ADR 0101: tombstones are kept 90 days, then purged by a daily job.
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddHostedService<SyncTombstonePurgeService>();
builder.Services.AddScoped<IProxyService, ProxyService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IContentBlobService, ContentBlobService>();
builder.Services.AddSingleton<IServerDatabaseBackupService, ServerDatabaseBackupService>();
builder.Services.AddSingleton<IProxyAddressResolver, SystemProxyAddressResolver>();
builder.Services.AddSingleton<IProxyConnectionPinAccessor, ProxyConnectionPinAccessor>();
builder.Services.AddSingleton<IProxyTcpConnector, SocketProxyTcpConnector>();
builder.Services.AddSingleton<IProxyStreamConnector, SocketProxyStreamConnector>();
builder.Services.AddScoped<IProxyGrantAuthority, ProxyGrantAuthority>();
// ADR 0191 §6: one-time pairing codes of an installed server.
builder.Services.AddLocalServer(builder.Configuration, builder.Environment.ContentRootPath);

// ── Network relay policy (ADR 0102 §5) ────────────────────────────────────────
// HackerOs:Proxy is read once at startup; ProxyRelayPolicy fills the defaults the binder cannot
// (it appends to lists initialized in code), and ProxyPolicyStartupReporter logs a warning per
// setting that weakens them.
builder.Services.AddOptions<ProxyOptions>().Bind(builder.Configuration.GetSection(ProxyOptions.SectionName));
builder.Services.AddSingleton(provider =>
    ProxyRelayPolicy.From(provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProxyOptions>>().Value));
builder.Services.AddHostedService<ProxyPolicyStartupReporter>();

// ── HTTP client for proxy outbound requests ───────────────────────────────────
// The proxy service uses a named client with strict socket timeouts.
builder.Services.AddHttpClient("proxy", client =>
{
    client.Timeout = TimeSpan.FromSeconds(35); // slightly > max proxy duration
})
.ConfigurePrimaryHttpMessageHandler(services => new SocketsHttpHandler
{
    // Never follow redirects automatically — the proxy service handles them manually
    // so it can enforce the server-side redirect limit and DNS rebinding check.
    AllowAutoRedirect = false,
    ConnectTimeout = TimeSpan.FromSeconds(10),
    // Disable automatic decompression to pass content hashes correctly.
    AutomaticDecompression = System.Net.DecompressionMethods.None,
    ConnectCallback = async (context, cancellationToken) =>
    {
        var pin = services.GetRequiredService<IProxyConnectionPinAccessor>().Address
            ?? throw new InvalidOperationException("A proxy connection was attempted without a validated address pin.");
        var socket = new System.Net.Sockets.Socket(pin.AddressFamily, System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new System.Net.IPEndPoint(pin, context.DnsEndPoint.Port), cancellationToken);
            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
});

// ── Authentication — Bearer token ─────────────────────────────────────────────
// Token validation uses the server's own token store, not a JWT public key.
// This avoids shipping a signing secret to the client.
builder.Services.AddAuthentication("HackerOsBearer")
    .AddScheme<HackerOsBearerOptions, HackerOsBearerHandler>("HackerOsBearer", _ => { });
builder.Services.AddAuthorization();

// ── CORS — allow the PWA origin ───────────────────────────────────────────────
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .WithMethods("GET", "POST", "PUT", "DELETE")
              .AllowCredentials());
});

// ── OpenAPI (Scalar UI) ───────────────────────────────────────────────────────
builder.Services.AddOpenApi();

var app = builder.Build();

// ── Middleware pipeline ───────────────────────────────────────────────────────
// ADR 0131, section 2: COOP/COEP on every response, so the OS's page and its Web Workers are
// cross-origin isolated (HackerOs:CrossOriginIsolation: auto, on or off). The PWA's service worker
// adds the same headers once it is installed; this covers the first load and a host without it.
IReadOnlyList<KeyValuePair<string, string>> isolationHeaders = HackerOs.Platform.Core.Isolation.CrossOriginIsolationPolicy.ServerHeaders(
    HackerOs.Platform.Core.Isolation.CrossOriginIsolationPolicy.Parse(app.Configuration[HackerOs.Platform.Core.Isolation.CrossOriginIsolationPolicy.SettingKey]));
if (isolationHeaders.Count > 0)
{
    app.Use((context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            foreach ((string name, string value) in isolationHeaders)
            {
                context.Response.Headers[name] = value;
            }

            return Task.CompletedTask;
        });
        return next(context);
    });
}

// ADR 0130: a page address of the OS (/, a deep link, a reload) is answered with the Ecosystem's
// index.html, served by MapStaticAssets like every other file of the OS, in every environment.
// The rewrite runs before routing; API and documentation paths and real files are left alone.
app.UseOsPageRewrite();
app.UseRouting();
// A published server has the page as a plain file only (ADR 0191: the installable server).
app.UseOsPageFile();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseCors();
// TCP/UDP relay streams (ADR 0102 §2, §3) are WebSockets; ping the client so an idle stream's
// carrier is not cut by intermediaries before the relay's own idle timeout.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseAuthentication();
app.UseAuthorization();
// ADR 0130: the OS in WebAssembly. MapStaticAssets serves every file the Ecosystem publishes
// (index.html with its fingerprinted references, _framework/, the service worker, the apps);
// only its hackeros-host.json is answered by this server instead (ADR 0137).
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

app.UseHostDiscoveryFile();

app.MapStaticAssets();

// ── Database migration on startup ─────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HackerOsServerDbContext>();
    // The server currently ships its model without generated EF migrations.
    // MigrateAsync is a no-op in that state, so a fresh SQLite file would look
    // connectable while containing no application tables. Use EnsureCreated for
    // this migration-free bootstrap; once migrations are introduced, the normal
    // migration path becomes authoritative.
    string[] migrations = db.Database.GetMigrations().ToArray();
    if (migrations.Length == 0)
    {
        await db.Database.EnsureCreatedAsync();
    }
    else
    {
        await db.Database.MigrateAsync();
    }
}

// ADR 0191: a command (`pair`) runs here, with the database ready, and the server does not start.
if (command is not null)
{
    return await command.RunAsync(app.Services, Console.Out);
}

// ── Endpoint groups ───────────────────────────────────────────────────────────
app.MapVersionEndpoints();    // GET /api/version, POST /api/version/check
app.MapIdentityEndpoints();   // POST /api/account, POST /api/auth/login, ...
app.MapSyncEndpoints();       // POST /api/sync/pull, POST /api/sync/push, ...
app.MapProxyEndpoints();      // POST /api/proxy/http, /tcp-probe; WS /api/proxy/tcp, /udp; GET /api/proxy/policy
app.MapAdminEndpoints();      // GET /health, GET /api/account/data-summary, ...

app.MapHostInfoEndpoints();   // GET /api/hackeros/host-info (ADR 0134)
app.MapLocalServerEndpoints(); // POST /api/auth/pair, GET /api/hackeros/local-ca.crt (ADR 0191)

await app.RunAsync();
return 0;

/// <summary>
/// Public application entry point marker used by the server integration tests.
/// The runtime behavior remains defined by the top-level program above.
/// </summary>
public partial class Program;
