using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using Kazo.Os.Server.Contracts.Proxy;
using Kazo.Os.Server.Services;

namespace Kazo.Os.Server.Endpoints;

/// <summary>
/// Carries one relayed TCP or UDP stream over a WebSocket (ADR 0102 §2, §3): the protocol is
/// described in <c>ProxyStreamContracts.cs</c>.
/// </summary>
public static class ProxyStreamWebSocketPump
{
    private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Validates and connects the stream, accepts the WebSocket, then relays until either side closes.</summary>
    /// <param name="context">The upgrade request.</param>
    /// <param name="proxy">The relay of this request scope.</param>
    /// <param name="protocol">TCP or UDP.</param>
    public static async Task<IResult> AcceptAsync(HttpContext context, IProxyService proxy, ProxyProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(proxy);
        if (!context.WebSockets.IsWebSocketRequest)
        {
            return Results.Json(
                new ProxyErrorResponse(null, ProxyErrorCode.MalformedRequest, "This route only accepts a WebSocket upgrade.", Guid.NewGuid()),
                ProxyContractsJsonContext.Default.ProxyErrorResponse,
                statusCode: StatusCodes.Status400BadRequest);
        }

        CancellationToken aborted = context.RequestAborted;
        string host = context.Request.Query[ProxyStreamRoutes.HostParameter].ToString();
        string appId = context.Request.Query[ProxyStreamRoutes.AppIdParameter].ToString();
        int port = int.TryParse(context.Request.Query[ProxyStreamRoutes.PortParameter], out int parsed) ? parsed : 0;
        // ADR 0235 §2: tls=1 asks the server to open TLS to the target before the WebSocket is accepted.
        bool useTls = context.Request.Query[ProxyStreamRoutes.TlsParameter].ToString() == "1";
        string? targetHost = context.Request.Query[ProxyStreamRoutes.TargetHostParameter].ToString() is { Length: > 0 } name ? name : null;

        // Connect first: the browser cannot read the status of a refused upgrade, so a refusal is
        // reported as the close code of an accepted WebSocket instead.
        IProxyStreamChannel? channel = null;
        ProxyRequestException? refusal = null;
        try
        {
            channel = await proxy.OpenStreamAsync(
                ProxyEndpoints.GetAccountId(context.User),
                ProxyEndpoints.GetDeviceId(context.User),
                protocol,
                new ProxyStreamRequest(host, port, appId) { UseTls = useTls, TargetHost = targetHost },
                aborted);
        }
        catch (ProxyRequestException exception)
        {
            refusal = exception;
        }

        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        if (refusal is not null || channel is null)
        {
            await CloseQuietlyAsync(
                socket,
                (WebSocketCloseStatus)ProxyStreamCloseCodes.Rejected,
                ProxyStreamException.ToCloseReason(refusal!.ErrorCode, refusal.Message));
            return Results.Empty;
        }

        await using (channel)
        {
            byte[] opened = JsonSerializer.SerializeToUtf8Bytes(channel.Remote, ProxyContractsJsonContext.Default.ProxyStreamOpened);
            await socket.SendAsync(opened, WebSocketMessageType.Text, endOfMessage: true, aborted);
            await RelayAsync(socket, channel, aborted);
        }

        return Results.Empty;
    }

    /// <summary>Pumps both directions until one ends, then closes the other with the right code.</summary>
    private static async Task RelayAsync(WebSocket socket, IProxyStreamChannel channel, CancellationToken aborted)
    {
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        Task<bool> upstream = PumpUpAsync(socket, channel, stop.Token);
        Task downstream = PumpDownAsync(socket, channel, stop.Token);
        Task first = await Task.WhenAny(upstream, downstream);

        if (first == downstream)
        {
            // The target closed, failed or went idle: say so with the channel's close code.
            ProxyStreamClosure closure = channel.Closure ?? new ProxyStreamClosure(ProxyStreamCloseCodes.RemoteClosed, null);
            await CloseQuietlyAsync(socket, (WebSocketCloseStatus)closure.CloseCode, closure.Reason ?? string.Empty);
        }
        else
        {
            // The client closed (or sent garbage): drop the socket, then finish the close handshake.
            bool tooBig = upstream.IsCompletedSuccessfully && !upstream.Result;
            await channel.DisposeAsync();
            await CloseQuietlyAsync(
                socket,
                tooBig ? WebSocketCloseStatus.MessageTooBig : WebSocketCloseStatus.NormalClosure,
                tooBig ? "message too big" : string.Empty);
        }

        await stop.CancelAsync();
        try
        {
            await Task.WhenAll(upstream, downstream);
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or InvalidOperationException or ObjectDisposedException)
        {
            // The other direction was cut short on purpose.
        }
    }

    /// <summary>WebSocket → socket. Returns <see langword="false"/> when a message exceeded the protocol's limit.</summary>
    private static async Task<bool> PumpUpAsync(WebSocket socket, IProxyStreamChannel channel, CancellationToken cancellationToken)
    {
        int limit = channel.Protocol == ProxyProtocol.Udp ? ProxyStreamRoutes.MaxDatagramBytes : ProxyStreamRoutes.MaxChunkBytes;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(limit + 1);
        try
        {
            while (true)
            {
                int length = 0;
                WebSocketReceiveResult result;
                do
                {
                    if (length > limit)
                    {
                        return false;
                    }

                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return true;
                    }

                    length += result.Count;
                }
                while (!result.EndOfMessage);

                if (length > limit)
                {
                    return false;
                }

                // Only binary frames carry data; a text frame from the client has no meaning.
                if (result.MessageType == WebSocketMessageType.Binary && (length > 0 || channel.Protocol == ProxyProtocol.Udp))
                {
                    await channel.SendAsync(buffer.AsMemory(0, length), cancellationToken);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Socket → WebSocket, one binary message per chunk or datagram.</summary>
    private static async Task PumpDownAsync(WebSocket socket, IProxyStreamChannel channel, CancellationToken cancellationToken)
    {
        while (await channel.ReceiveAsync(cancellationToken) is { } data)
        {
            await socket.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
        }
    }

    private static async Task CloseQuietlyAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        using CancellationTokenSource timeout = new(CloseHandshakeTimeout);
        try
        {
            await socket.CloseAsync(status, reason, timeout.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
            // The client went away without finishing the close handshake.
        }
    }
}
