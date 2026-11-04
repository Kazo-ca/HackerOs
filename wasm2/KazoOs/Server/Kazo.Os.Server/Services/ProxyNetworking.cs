using System.Net;
using System.Net.Sockets;
using Kazo.Os.Server.Contracts.Proxy;

namespace Kazo.Os.Server.Services;

/// <summary>Resolves proxy destinations through an injectable, testable boundary.</summary>
public interface IProxyAddressResolver
{
    /// <summary>Returns every address currently published for <paramref name="host"/>.</summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>Uses the operating-system DNS resolver for production proxy requests.</summary>
public sealed class SystemProxyAddressResolver : IProxyAddressResolver
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(
        string host,
        CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>
/// Carries the server-validated destination address to the socket connection callback.
/// Async-local state keeps concurrent requests isolated while allowing pooled handlers.
/// </summary>
public interface IProxyConnectionPinAccessor
{
    /// <summary>Gets the address authorized for the current asynchronous request flow.</summary>
    IPAddress? Address { get; }

    /// <summary>Temporarily pins the current flow to an already validated address.</summary>
    IDisposable Push(IPAddress address);
}

/// <inheritdoc />
public sealed class ProxyConnectionPinAccessor : IProxyConnectionPinAccessor
{
    private readonly AsyncLocal<IPAddress?> _current = new();

    /// <inheritdoc />
    public IPAddress? Address => _current.Value;

    /// <inheritdoc />
    public IDisposable Push(IPAddress address)
    {
        var previous = _current.Value;
        _current.Value = address;
        return new RestoreScope(() => _current.Value = previous);
    }

    private sealed class RestoreScope(Action restore) : IDisposable
    {
        private Action? _restore = restore;

        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}

/// <summary>
/// Attempts a single TCP connect against an already-validated address, through an injectable,
/// testable boundary (ADR 0035). Never sends or receives application data — the connect attempt
/// itself is the entire probe.
/// </summary>
public interface IProxyTcpConnector
{
    /// <summary>Attempts one TCP connect within <paramref name="timeout"/> and reports the outcome.</summary>
    Task<ProxyTcpProbeState> ProbeAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Uses a real OS socket connect for production TCP probes.</summary>
public sealed class SocketProxyTcpConnector : IProxyTcpConnector
{
    /// <inheritdoc />
    public async Task<ProxyTcpProbeState> ProbeAsync(
        IPAddress address, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await socket.ConnectAsync(address, port, timeoutCts.Token).ConfigureAwait(false);
            return ProxyTcpProbeState.Open;
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return ProxyTcpProbeState.Closed;
        }
        catch (SocketException)
        {
            return ProxyTcpProbeState.Filtered;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timed out, not caller-cancelled — the target didn't respond in time.
            return ProxyTcpProbeState.Filtered;
        }
    }
}
