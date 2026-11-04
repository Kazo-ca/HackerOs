using System.Net.Http;
using System.Net.Sockets;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.AppSdk.Net;

/// <summary>
/// Turns gateway outcomes into the exceptions the matching <c>System.Net</c> API throws, so code
/// written for .NET reads the same against Kazo OS.
/// </summary>
internal static class KazoNetworkErrorTranslator
{
    /// <summary>Gets the ambient app's network gateway.</summary>
    public static IAppNetworkGateway Gateway => KazoExecutionContext.Current.Network;

    /// <summary>The <see cref="SocketError"/> a socket API reports for <paramref name="error"/>.</summary>
    public static SocketError ToSocketError(KazoNetworkError error) => error switch
    {
        KazoNetworkError.HostNotFound => SocketError.HostNotFound,
        KazoNetworkError.HostUnreachable => SocketError.HostUnreachable,
        KazoNetworkError.ConnectionRefused => SocketError.ConnectionRefused,
        KazoNetworkError.ConnectionFailed or KazoNetworkError.TimedOut => SocketError.TimedOut,
        KazoNetworkError.BlockedByAdministrator => SocketError.AccessDenied,
        _ => SocketError.SocketError
    };

    /// <summary>Returns the value, or throws the <see cref="SocketException"/> of a DNS or socket API.</summary>
    public static T UnwrapSocket<T>(KazoNetworkResult<T> result) =>
        result.Succeeded ? result.Value! : throw new SocketException((int)ToSocketError(result.Error));

    /// <summary>Returns the value, or throws <see cref="KazoHttpRequestException"/> as <c>HttpClient</c> does.</summary>
    public static T UnwrapHttp<T>(KazoNetworkResult<T> result, Uri requestUri)
    {
        if (result.Succeeded)
        {
            return result.Value!;
        }

        HttpRequestError httpError = result.Error switch
        {
            KazoNetworkError.HostNotFound => HttpRequestError.NameResolutionError,
            KazoNetworkError.TooManyRedirects => HttpRequestError.Unknown,
            _ => HttpRequestError.ConnectionError
        };
        string message = result.Message ?? result.Error switch
        {
            KazoNetworkError.HostNotFound => $"Could not resolve host: {requestUri.Host}",
            KazoNetworkError.TooManyRedirects => "Maximum (10) redirects followed",
            KazoNetworkError.TimedOut => $"Connection to {requestUri.Host} timed out",
            KazoNetworkError.BlockedByAdministrator => $"Access to {requestUri.Host} is blocked by the administrator",
            _ => $"Failed to connect to {requestUri.Host}"
        };
        throw new KazoHttpRequestException(result.Error, httpError, message);
    }

    /// <summary>Runs a gateway call and maps a capability denial to <see cref="UnauthorizedAccessException"/>.</summary>
    public static async ValueTask<T> CallAsync<T>(Func<IAppNetworkGateway, ValueTask<T>> call)
    {
        IAppNetworkGateway gateway = Gateway;
        try
        {
            return await call(gateway).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException denied)
        {
            throw new UnauthorizedAccessException(
                $"The app does not hold the '{denied.Capability}' capability needed for this network call.", denied);
        }
    }
}
