using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Platform.Core.ServerConnection;
using Kazo.Os.Server.Contracts.Proxy;
using Xunit;

namespace Kazo.Os.Network.Tests;

/// <summary>
/// ADR 0102 on the OS side: request bodies relayed to the real network, and the TCP/UDP sockets
/// of <see cref="HackTcpClient.ConnectAsync"/> and <see cref="HackUdpClient"/> over the relay.
/// </summary>
public sealed partial class Wave4NetworkTests
{
    [Fact]
    public async Task HackHttpClient_post_to_a_real_host_carries_its_body_through_the_relay()
    {
        RecordingStreamProxyClient relay = new();
        UseServer(new ConnectedServerConnectionService(), relay);
        EnterApp(new GrantAllCapabilityChecker());
        using HackHttpClient client = new();

        HackHttpResponse response = await client.PostAsync(
            "https://real-external-site.example/api", new HackStringContent("{\"q\":1}", "application/json"));

        ProxyHttpRequest sent = Assert.Single(relay.HttpRequests);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("POST", sent.HttpMethod);
        Assert.Equal("{\"q\":1}", Encoding.UTF8.GetString(sent.Body!));
        Assert.Equal("application/json; charset=utf-8", sent.ContentType);
        Assert.Equal(sent.Body!.Length, sent.BodyBytes);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sent.Body)).ToLowerInvariant(), sent.BodyHash);
        Assert.True(sent.IncludeBody);
    }

    [Fact]
    public async Task HackTcpClient_connect_round_trips_bytes_through_the_relay()
    {
        RecordingStreamProxyClient relay = new();
        UseServer(new ConnectedServerConnectionService(), relay);
        EnterApp(new GrantAllCapabilityChecker());

        await using HackNetworkStream stream = await HackTcpClient.ConnectAsync("real-external-site.example", 443);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("hello"));
        byte[] buffer = new byte[16];
        int read = await stream.ReadAsync(buffer);

        Assert.Equal("hello", Encoding.ASCII.GetString(buffer, 0, read));
        Assert.Equal(new IPEndPoint(IPAddress.Parse("93.184.216.34"), 443), stream.RemoteEndPoint);
        Assert.Equal((ProxyProtocol.Tcp, "real-external-site.example", 443), relay.Streams.Single());
    }

    [Fact]
    public async Task HackNetworkStream_reads_zero_once_the_peer_closed()
    {
        RecordingStreamProxyClient relay = new();
        UseServer(new ConnectedServerConnectionService(), relay);
        EnterApp(new GrantAllCapabilityChecker());

        await using HackNetworkStream stream = await HackTcpClient.ConnectAsync("real-external-site.example", 443);
        relay.LastChannel!.CloseFromRemote();

        Assert.Equal(0, await stream.ReadAsync(new byte[4]));
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
    }

    [Fact]
    public async Task HackTcpClient_connect_to_a_simulated_host_is_refused()
    {
        UseServer(new ConnectedServerConnectionService(), new RecordingStreamProxyClient());
        EnterApp(new GrantAllCapabilityChecker());

        SocketException refused = await Assert.ThrowsAsync<SocketException>(() => HackTcpClient.ConnectAsync("hackersearch.net", 80));

        Assert.Equal(SocketError.ConnectionRefused, refused.SocketErrorCode);
    }

    [Fact]
    public async Task HackTcpClient_connect_without_a_server_finds_no_host()
    {
        EnterApp(new GrantAllCapabilityChecker());

        SocketException unknown = await Assert.ThrowsAsync<SocketException>(() => HackTcpClient.ConnectAsync("real-external-site.example", 443));

        Assert.Equal(SocketError.HostNotFound, unknown.SocketErrorCode);
    }

    [Fact]
    public async Task HackTcpClient_connect_without_network_real_access_never_reaches_the_relay()
    {
        RecordingStreamProxyClient relay = new();
        UseServer(new ConnectedServerConnectionService(), relay);
        EnterApp(new GrantAllCapabilityChecker(AppCapabilities.NetworkRealAccess));

        SocketException unknown = await Assert.ThrowsAsync<SocketException>(() => HackTcpClient.ConnectAsync("real-external-site.example", 443));

        Assert.Equal(SocketError.HostNotFound, unknown.SocketErrorCode);
        Assert.Empty(relay.Streams);
    }

    [Fact]
    public async Task HackTcpClient_connect_needs_network_simulated_write()
    {
        EnterApp(new GrantAllCapabilityChecker(AppCapabilities.NetworkSimulatedWrite));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => HackTcpClient.ConnectAsync("real-external-site.example", 443));
    }

    [Theory]
    [InlineData(ProxyErrorCode.BlockedPort, SocketError.ConnectionRefused)]
    [InlineData(ProxyErrorCode.ConnectionRefused, SocketError.ConnectionRefused)]
    [InlineData(ProxyErrorCode.HostUnreachable, SocketError.HostUnreachable)]
    [InlineData(ProxyErrorCode.GrantMissing, SocketError.HostNotFound)]
    [InlineData(ProxyErrorCode.RelayDisabled, SocketError.HostNotFound)]
    public async Task HackTcpClient_maps_a_relay_refusal_to_the_socket_error_of_a_real_stack(string errorCode, SocketError expected)
    {
        UseServer(new ConnectedServerConnectionService(), new RecordingStreamProxyClient { Refusal = errorCode });
        EnterApp(new GrantAllCapabilityChecker());

        SocketException failure = await Assert.ThrowsAsync<SocketException>(() => HackTcpClient.ConnectAsync("real-external-site.example", 8443));

        Assert.Equal(expected, failure.SocketErrorCode);
    }

    [Fact]
    public async Task HackUdpClient_sends_and_receives_one_datagram_through_the_relay()
    {
        RecordingStreamProxyClient relay = new();
        UseServer(new ConnectedServerConnectionService(), relay);
        EnterApp(new GrantAllCapabilityChecker());

        await using HackUdpClient udp = await HackUdpClient.ConnectAsync("ntp.real-external-site.example", 123);
        int sent = await udp.SendAsync(new byte[48]);
        HackUdpReceiveResult reply = await udp.ReceiveAsync();

        Assert.Equal(48, sent);
        Assert.Equal(48, reply.Buffer.Length);
        Assert.Equal(123, reply.RemoteEndPoint.Port);
        Assert.Equal((ProxyProtocol.Udp, "ntp.real-external-site.example", 123), relay.Streams.Single());
        await Assert.ThrowsAsync<ArgumentException>(() => udp.SendAsync(new byte[65_508]).AsTask());
    }

    /// <summary>
    /// A relay that answers HTTP with 200, records each request, and opens in-memory echo streams
    /// (or refuses them with <see cref="Refusal"/>).
    /// </summary>
    private sealed class RecordingStreamProxyClient : IProxyClient
    {
        public List<ProxyHttpRequest> HttpRequests { get; } = [];

        public List<(ProxyProtocol Protocol, string Host, int Port)> Streams { get; } = [];

        public string? Refusal { get; init; }

        public EchoChannel? LastChannel { get; private set; }

        public Task<ProxyHttpResponse> ExecuteHttpRequestAsync(
            Uri serverBaseUrl, string accessToken, ProxyHttpRequest request, CancellationToken cancellationToken = default)
        {
            HttpRequests.Add(request);
            return Task.FromResult(new ProxyHttpResponse(request.RequestId, 200, "OK", [], null, 0, request.TargetUrl, 0, 5));
        }

        public Task<ProxyTcpProbeResponse> ExecuteTcpProbeAsync(
            Uri serverBaseUrl, string accessToken, ProxyTcpProbeRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<ProxyPolicyResponse> GetPolicyAsync(
            Uri serverBaseUrl, string accessToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IProxyStreamChannel> OpenStreamAsync(
            Uri serverBaseUrl, string accessToken, ProxyProtocol protocol, ProxyStreamRequest request, CancellationToken cancellationToken = default)
        {
            Streams.Add((protocol, request.Host, request.Port));
            if (Refusal is not null)
            {
                throw new ProxyStreamException(Refusal, "refused by the test relay");
            }

            LastChannel = new EchoChannel(protocol, new ProxyStreamOpened("93.184.216.34", request.Port));
            return Task.FromResult<IProxyStreamChannel>(LastChannel);
        }
    }

    /// <summary>An in-memory stream that sends every message back.</summary>
    private sealed class EchoChannel(ProxyProtocol protocol, ProxyStreamOpened remote) : IProxyStreamChannel
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();

        public ProxyProtocol Protocol { get; } = protocol;

        public ProxyStreamOpened Remote { get; } = remote;

        public ProxyStreamClosure? Closure { get; private set; }

        public void CloseFromRemote()
        {
            Closure = new ProxyStreamClosure(ProxyStreamCloseCodes.RemoteClosed, "test");
            _inbound.Writer.TryComplete();
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            if (Closure is not null)
            {
                throw new InvalidOperationException("The stream is closed.");
            }

            _inbound.Writer.TryWrite(data.ToArray());
            return ValueTask.CompletedTask;
        }

        public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default) =>
            await _inbound.Reader.WaitToReadAsync(cancellationToken) && _inbound.Reader.TryRead(out byte[]? data) ? data : null;

        public ValueTask DisposeAsync()
        {
            Closure ??= new ProxyStreamClosure(1000, "closed by the client");
            _inbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
