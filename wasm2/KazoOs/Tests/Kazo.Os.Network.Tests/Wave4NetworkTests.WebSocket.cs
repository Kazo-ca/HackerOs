using System.Net;
using System.Net.WebSockets;
using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Commands.Ping;
using Kazo.Os.Platform.Core.Metrics;
using Kazo.Os.Platform.Core.Network;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kazo.Os.Network.Tests;

/// <summary>
/// ADR 0235 §1: <see cref="KazoClientWebSocket"/> over <see cref="AppNetworkGateway"/>, against a
/// real WebSocket server on the loopback interface (Kestrel), with no Kazo OS server.
/// </summary>
public sealed partial class Wave4NetworkTests
{
    /// <summary>The real host name the app connects to; the test connector sends it to the test server.</summary>
    private const string RealWebSocketHost = "irc.real-external-site.example";

    /// <summary>Makes an app ambient whose gateway opens real WebSockets to <paramref name="server"/>.</summary>
    private void EnterWebSocketApp(
        ICapabilityChecker capabilities,
        EchoWebSocketServer server,
        NetworkInterfaceStatistics? statistics = null,
        ProcessIoAccounting? io = null)
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new PingApp());
        AppNetworkGateway gateway = new(_network, capabilities, manifest.Id, statistics: statistics, processIo: io, pid: MeteredPid)
        {
            WebSocketConnector = server.ConnectAsync
        };
        KazoExecutionContext.Enter(new StubAppExecutionContext(manifest, gateway));
    }

    [Fact]
    public async Task KazoClientWebSocket_exchanges_text_frames_with_a_real_server_and_negotiates_the_sub_protocol()
    {
        await using EchoWebSocketServer server = await EchoWebSocketServer.StartAsync();
        EnterWebSocketApp(new GrantAllCapabilityChecker(), server);
        using KazoClientWebSocket socket = new();
        socket.Options.AddSubProtocol("text.ircv3.net");

        await socket.ConnectAsync(new Uri($"wss://{RealWebSocketHost}/webirc"));
        await socket.SendAsync(Encoding.UTF8.GetBytes("NICK kazo"), WebSocketMessageType.Text, endOfMessage: true);
        byte[] buffer = new byte[64];
        ValueWebSocketReceiveResult echo = await socket.ReceiveAsync(buffer.AsMemory());
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

        Assert.Equal("text.ircv3.net", socket.SubProtocol);
        Assert.Equal(WebSocketMessageType.Text, echo.MessageType);
        Assert.True(echo.EndOfMessage);
        Assert.Equal("echo:NICK kazo", Encoding.UTF8.GetString(buffer, 0, echo.Count));
        Assert.Equal(WebSocketState.Closed, socket.State);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);
        Assert.Equal(new Uri($"wss://{RealWebSocketHost}/webirc"), Assert.Single(server.Requested));
    }

    [Fact]
    public async Task KazoClientWebSocket_without_network_real_access_throws_unauthorized_and_opens_nothing()
    {
        await using EchoWebSocketServer server = await EchoWebSocketServer.StartAsync();
        EnterWebSocketApp(new GrantAllCapabilityChecker(AppCapabilities.NetworkRealAccess), server);
        using KazoClientWebSocket socket = new();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => socket.ConnectAsync(new Uri($"wss://{RealWebSocketHost}/")));

        Assert.Empty(server.Requested);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    [Theory]
    [InlineData("ws://hackersearch.net/chat")]
    [InlineData("ws://localhost:8080/")]
    [InlineData("wss://intranet.hack/")]
    public async Task KazoClientWebSocket_refuses_a_simulated_host_as_faulted_without_reaching_the_browser(string address)
    {
        await using EchoWebSocketServer server = await EchoWebSocketServer.StartAsync();
        EnterWebSocketApp(new GrantAllCapabilityChecker(), server);
        using KazoClientWebSocket socket = new();

        WebSocketException refused = await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(new Uri(address)));

        Assert.Equal(WebSocketError.Faulted, refused.WebSocketErrorCode);
        Assert.Contains("simulated host", refused.Message, StringComparison.Ordinal);
        Assert.Contains("#393", refused.Message, StringComparison.Ordinal);
        Assert.Empty(server.Requested);
    }

    [Fact]
    public async Task KazoClientWebSocket_reports_a_refused_handshake_as_faulted_without_detail()
    {
        await using EchoWebSocketServer server = await EchoWebSocketServer.StartAsync();
        EnterWebSocketApp(new GrantAllCapabilityChecker(), server);
        using KazoClientWebSocket socket = new();

        // The test server answers 404 to any path but /webirc, as a server that refuses the upgrade.
        WebSocketException refused = await Assert.ThrowsAsync<WebSocketException>(
            () => socket.ConnectAsync(new Uri($"wss://{RealWebSocketHost}/not-a-socket")));

        Assert.Equal(WebSocketError.Faulted, refused.WebSocketErrorCode);
        Assert.DoesNotContain("404", refused.Message, StringComparison.Ordinal);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    [Fact]
    public async Task KazoClientWebSocket_frames_are_counted_on_eth0_and_charged_to_the_process()
    {
        NetworkInterfaceStatistics statistics = new();
        ProcessIoAccounting io = new();
        await using EchoWebSocketServer server = await EchoWebSocketServer.StartAsync();
        EnterWebSocketApp(new GrantAllCapabilityChecker(), server, statistics, io);
        using KazoClientWebSocket socket = new();

        await socket.ConnectAsync(new Uri($"wss://{RealWebSocketHost}/webirc"));
        NetworkInterfaceCounters afterHandshake = statistics.GetInterfaces()[1];
        await socket.SendAsync(Encoding.UTF8.GetBytes("PING :kazo"), WebSocketMessageType.Text, endOfMessage: true);
        await socket.ReceiveAsync(new byte[64].AsMemory());
        NetworkInterfaceCounters eth0 = statistics.GetInterfaces()[1];

        // Handshake: SYN and the HTTP Upgrade out, SYN-ACK and 101 back.
        Assert.Equal(2, afterHandshake.TransmitPackets);
        Assert.Equal(2, afterHandshake.ReceivePackets);
        // "PING :kazo" (10 bytes) in a masked frame: 20 IP + 20 TCP + 6 frame header + 10.
        Assert.Equal(afterHandshake.TransmitBytes + 56, eth0.TransmitBytes);
        // "echo:PING :kazo" (15 bytes) in an unmasked frame: 20 + 20 + 2 + 15.
        Assert.Equal(afterHandshake.ReceiveBytes + 57, eth0.ReceiveBytes);
        ProcessIoCounters process = io.Get(MeteredPid);
        Assert.Equal(eth0.TransmitBytes, process.WrittenChars);
        Assert.Equal(eth0.ReceiveBytes, process.ReadChars);
    }

    [Fact]
    public async Task KazoClientWebSocket_behaves_like_ClientWebSocket_before_a_connect()
    {
        EnterApp(new GrantAllCapabilityChecker());
        using KazoClientWebSocket socket = new();

        Assert.Equal(WebSocketState.None, socket.State);
        Assert.Throws<ArgumentException>(() => socket.Options.AddSubProtocol("bad protocol"));
        await Assert.ThrowsAsync<ArgumentException>(() => socket.ConnectAsync(new Uri("https://example.org/")));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => socket.SendAsync(new byte[1], WebSocketMessageType.Text, true, CancellationToken.None));
    }

    [Fact]
    public async Task KazoClientWebSocket_in_a_context_without_websockets_is_not_supported()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new PingApp());
        KazoExecutionContext.Enter(new StubAppExecutionContext(manifest, new NoWebSocketGateway()));
        using KazoClientWebSocket socket = new();

        await Assert.ThrowsAsync<NotSupportedException>(() => socket.ConnectAsync(new Uri($"wss://{RealWebSocketHost}/")));
    }

    /// <summary>A gateway that predates WebSockets, as the Web Worker one still is.</summary>
    private sealed class NoWebSocketGateway : IAppNetworkGateway
    {
        public ValueTask<KazoNetworkResult<KazoHostEntry>> ResolveAsync(string hostName, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public ValueTask<KazoNetworkResult<KazoHostEntry>> ReverseLookupAsync(IPAddress address, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public ValueTask<KazoNetworkResult<KazoPingReply>> PingAsync(
            string hostNameOrAddress, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public ValueTask<KazoNetworkResult<KazoHttpResponse>> SendAsync(KazoHttpRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public ValueTask<KazoNetworkResult<IReadOnlyList<KazoPortProbeResult>>> ProbeTcpAsync(
            string hostNameOrAddress, int firstPort, int lastPort, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public ValueTask<KazoNetworkResult<KazoOsFingerprint?>> DetectOperatingSystemAsync(
            string hostNameOrAddress, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    /// <summary>
    /// A real WebSocket server on 127.0.0.1 (Kestrel): <c>/webirc</c> accepts the first offered
    /// sub-protocol and echoes each text message prefixed with <c>echo:</c>; any other path is 404.
    /// </summary>
    private sealed class EchoWebSocketServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly Uri _baseAddress;
        private readonly List<Uri> _requested = [];

        private EchoWebSocketServer(WebApplication app, Uri baseAddress)
        {
            _app = app;
            _baseAddress = baseAddress;
        }

        /// <summary>Gets the addresses the OS asked to open, in order.</summary>
        public IReadOnlyList<Uri> Requested
        {
            get
            {
                lock (_requested)
                {
                    return [.. _requested];
                }
            }
        }

        public static async Task<EchoWebSocketServer> StartAsync()
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            WebApplication app = builder.Build();
            app.UseWebSockets();
            app.Map("/webirc", static async (HttpContext context) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                string? subProtocol = context.WebSockets.WebSocketRequestedProtocols.FirstOrDefault();
                using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(subProtocol);
                byte[] buffer = new byte[1024];
                while (true)
                {
                    WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, received.CloseStatusDescription, CancellationToken.None);
                        return;
                    }

                    byte[] reply = [.. "echo:"u8.ToArray(), .. buffer.AsSpan(0, received.Count)];
                    await socket.SendAsync(reply, WebSocketMessageType.Text, endOfMessage: true, context.RequestAborted);
                }
            });
            await app.StartAsync();
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new EchoWebSocketServer(app, new Uri(address));
        }

        /// <summary>The gateway's connector: records the real address, then opens the test server's path.</summary>
        public async Task<WebSocket> ConnectAsync(Uri uri, IReadOnlyList<string> subProtocols, CancellationToken cancellationToken)
        {
            lock (_requested)
            {
                _requested.Add(uri);
            }

            ClientWebSocket socket = new();
            foreach (string subProtocol in subProtocols)
            {
                socket.Options.AddSubProtocol(subProtocol);
            }

            try
            {
                await socket.ConnectAsync(new Uri($"ws://{_baseAddress.Authority}{uri.PathAndQuery}"), cancellationToken);
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
