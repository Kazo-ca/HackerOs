using System.Net;
using System.Net.Sockets;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Commands.Ping;
using Kazo.Os.Platform.Core.Network;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Xunit;

namespace Kazo.Os.Network.Tests;

/// <summary>
/// ADR 0056 section 6: the SDK network facades (<see cref="KazoDns"/>, <see cref="KazoPing"/>,
/// <see cref="KazoHttpClient"/>, <see cref="KazoTcpClient"/>) over the OS-owned
/// <see cref="AppNetworkGateway"/>, including the capability checks and the real-network fallback
/// decision the apps no longer make themselves.
/// </summary>
public sealed partial class Wave4NetworkTests
{
    /// <summary>Makes an app with the given gateway ambient for the rest of the calling test.</summary>
    private void EnterApp(ICapabilityChecker capabilities)
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new PingApp());
        KazoExecutionContext.Enter(new StubAppExecutionContext(
            manifest, new AppNetworkGateway(_network, capabilities, manifest.Id, _serverConnection, _proxy)));
    }

    [Fact]
    public async Task KazoDns_resolves_simulated_names_and_rejects_unknown_ones_like_getaddrinfo()
    {
        EnterApp(new GrantAllCapabilityChecker());

        IPAddress[] addresses = await KazoDns.GetHostAddressesAsync("hackersearch.net");
        KazoHostEntry reverse = await KazoDns.GetHostEntryAsync(IPAddress.Parse("192.168.1.90"));
        SocketException unknown = await Assert.ThrowsAsync<SocketException>(
            () => KazoDns.GetHostAddressesAsync("unknown-host-12345.com"));

        Assert.Equal([IPAddress.Parse("192.168.1.90")], addresses);
        Assert.Equal("hackersearch.net", reverse.HostName);
        Assert.Equal(SocketError.HostNotFound, unknown.SocketErrorCode);
    }

    [Fact]
    public async Task KazoPing_reports_a_down_host_as_unreachable_and_an_unknown_one_as_an_exception()
    {
        EnterApp(new GrantAllCapabilityChecker());

        KazoPingReply down = await KazoPing.SendAsync("hackerz-search.net");
        KazoPingException unknown = await Assert.ThrowsAsync<KazoPingException>(() => KazoPing.SendAsync("unknown-host-12345.com"));

        Assert.Equal(KazoIPStatus.DestinationHostUnreachable, down.Status);
        Assert.IsType<SocketException>(unknown.InnerException);
    }

    [Fact]
    public async Task KazoHttpClient_post_follows_the_login_redirect_and_keeps_the_session_cookie()
    {
        EnterApp(new GrantAllCapabilityChecker());
        using KazoHttpClient client = new();

        KazoHttpResponse login = await client.PostAsync(
            "https://hackmail.com/login",
            [new("username", "agent"), new("password", "secret123")]);
        KazoHttpResponse inbox = await client.GetAsync("https://hackmail.com/inbox");

        Assert.Equal(1, login.RedirectCount);
        Assert.Contains(client.Cookies.GetAllCookies(), cookie => cookie is { Domain: "hackmail.com", Name: "hackmail_session", Value: "user=agent" });
        Assert.Equal("KazoMail — Inbox", inbox.Page?.Title);
        Assert.Contains("Title: KazoMail — Inbox", inbox.ReadAsString());
    }

    [Fact]
    public async Task KazoHttpClient_sends_an_explicit_cookie_header_to_a_simulated_site_without_storing_it()
    {
        EnterApp(new GrantAllCapabilityChecker());
        using KazoHttpClient client = new();
        client.DefaultRequestHeaders["Cookie"] = "hackmail_session=user=agent";

        KazoHttpResponse inbox = await client.GetAsync("https://hackmail.com/inbox");

        // The value keeps its own '=': only the first one splits name from value.
        Assert.Equal("KazoMail — Inbox", inbox.Page?.Title);
        Assert.Equal(0, inbox.RedirectCount);
        Assert.Empty(inbox.SetCookies);
        Assert.Empty(client.Cookies.GetAllCookies());
    }

    [Fact]
    public async Task KazoHttpClient_resolves_relative_uris_against_its_base_address()
    {
        EnterApp(new GrantAllCapabilityChecker());
        using KazoHttpClient client = new() { BaseAddress = new Uri("https://hackmail.com/") };

        KazoHttpResponse relative = await client.GetAsync("login");
        KazoHttpResponse absolute = await client.GetAsync("https://hackmail.com/login");
        ArgumentException missingBase = await Assert.ThrowsAsync<ArgumentException>(() => new KazoHttpClient().GetAsync("login"));

        Assert.Equal(new Uri("https://hackmail.com/login"), relative.RequestUri);
        Assert.Equal(absolute.StatusCode, relative.StatusCode);
        Assert.Equal(absolute.ReadAsString(), relative.ReadAsString());
        Assert.NotNull(missingBase);
    }

    [Fact]
    public async Task KazoHttpClient_unknown_host_throws_name_resolution_error_with_browser_code()
    {
        EnterApp(new GrantAllCapabilityChecker());
        using KazoHttpClient client = new();

        KazoHttpRequestException exception = await Assert.ThrowsAsync<KazoHttpRequestException>(
            () => client.GetAsync("https://unknown-host-12345.com/"));

        Assert.Equal(System.Net.Http.HttpRequestError.NameResolutionError, exception.HttpRequestError);
        Assert.Equal("ERR_NAME_NOT_RESOLVED", exception.ErrorCode);
    }

    [Fact]
    public async Task Post_needs_network_simulated_write()
    {
        EnterApp(new GrantAllCapabilityChecker(AppCapabilities.NetworkSimulatedWrite));
        using KazoHttpClient client = new();

        UnauthorizedAccessException denied = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => client.PostAsync("https://hackmail.com/login", [new("username", "agent")]));

        Assert.IsType<AppGatewayAccessDeniedException>(denied.InnerException);
        Assert.Equal(200, (await client.GetAsync("https://hackersearch.net")).StatusCode);
    }

    [Fact]
    public async Task Real_fallback_is_never_taken_without_network_real_access_even_when_connected()
    {
        // UnusedProxyClient throws NotSupportedException if called: reaching "not found" proves the
        // OS refused the real route on the missing capability, not on the server state.
        UseServer(new ConnectedServerConnectionService(), new UnusedProxyClient());
        EnterApp(new GrantAllCapabilityChecker(AppCapabilities.NetworkRealAccess));
        using KazoHttpClient client = new();

        KazoHttpRequestException exception = await Assert.ThrowsAsync<KazoHttpRequestException>(
            () => client.GetAsync("https://real-external-site.example/"));
        SocketException probe = await Assert.ThrowsAsync<SocketException>(
            () => KazoTcpClient.ProbeAsync("real-external-site.example", 443, 443));

        Assert.Equal(KazoNetworkError.HostNotFound, exception.NetworkError);
        Assert.Equal(SocketError.HostNotFound, probe.SocketErrorCode);
    }

    [Fact]
    public async Task Real_fallback_relays_get_through_the_server_without_exposing_it()
    {
        UseServer(new ConnectedServerConnectionService(), new SuccessBodyProxyClient(200, "hello from the real internet"));
        EnterApp(new GrantAllCapabilityChecker());
        using KazoHttpClient client = new();

        string body = await client.GetStringAsync("https://real-external-site.example/");

        Assert.Equal("hello from the real internet", body);
    }

    [Fact]
    public async Task KazoTcpClient_scans_a_simulated_host_and_detects_its_os()
    {
        EnterApp(new GrantAllCapabilityChecker());

        IReadOnlyList<KazoPortProbeResult> ports = await KazoTcpClient.ProbeAsync("localhost", 20, 100);
        KazoOsFingerprint? os = await KazoTcpClient.DetectOperatingSystemAsync("localhost");

        Assert.Contains(ports, port => port is { Port: 22, State: KazoPortState.Open, ServiceName: "ssh" });
        Assert.DoesNotContain(ports, port => port.Port == 3306);
        Assert.Equal(_network.GetHost("localhost")!.OsFingerprint?.Name, os?.Name);
    }

    [Fact]
    public async Task Facades_outside_any_app_scope_fail_instead_of_borrowing_an_identity()
    {
        await Assert.ThrowsAsync<KazoExecutionContextMissingException>(() => KazoDns.GetHostAddressesAsync("hackersearch.net"));
    }
}
