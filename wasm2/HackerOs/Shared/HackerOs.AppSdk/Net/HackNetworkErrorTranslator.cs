using System.Net.Http;
using System.Net.Sockets;
using HackerOs.Simulation.Abstractions.Gateways;

namespace HackerOs.AppSdk.Net;

/// <summary>
/// Turns gateway outcomes into the exceptions the matching <c>System.Net</c> API throws, so code
/// written for .NET reads the same against HackerOS.
/// </summary>
internal static class HackNetworkErrorTranslator
{
    /// <summary>Gets the ambient app's network gateway.</summary>
    public static IAppNetworkGateway Gateway => HackExecutionContext.Current.Network;

    /// <summary>The <see cref="SocketError"/> a socket API reports for <paramref name="error"/>.</summary>
    public static SocketError ToSocketError(HackNetworkError error) => error switch
    {
        HackNetworkError.HostNotFound => SocketError.HostNotFound,
        HackNetworkError.HostUnreachable => SocketError.HostUnreachable,
        HackNetworkError.ConnectionRefused => SocketError.ConnectionRefused,
        HackNetworkError.ConnectionFailed or HackNetworkError.TimedOut => SocketError.TimedOut,
        HackNetworkError.BlockedByAdministrator => SocketError.AccessDenied,
        _ => SocketError.SocketError
    };

    /// <summary>Returns the value, or throws the <see cref="SocketException"/> of a DNS or socket API.</summary>
    public static T UnwrapSocket<T>(HackNetworkResult<T> result) =>
        result.Succeeded ? result.Value! : throw new SocketException((int)ToSocketError(result.Error));

    /// <summary>Returns the value, or throws <see cref="HackHttpRequestException"/> as <c>HttpClient</c> does.</summary>
    public static T UnwrapHttp<T>(HackNetworkResult<T> result, Uri requestUri)
    {
        if (result.Succeeded)
        {
            return result.Value!;
        }

        HttpRequestError httpError = result.Error switch
        {
            HackNetworkError.HostNotFound => HttpRequestError.NameResolutionError,
            HackNetworkError.TooManyRedirects => HttpRequestError.Unknown,
            _ => HttpRequestError.ConnectionError
        };
        string message = result.Message ?? result.Error switch
        {
            HackNetworkError.HostNotFound => $"Could not resolve host: {requestUri.Host}",
            HackNetworkError.TooManyRedirects => "Maximum (10) redirects followed",
            HackNetworkError.TimedOut => $"Connection to {requestUri.Host} timed out",
            HackNetworkError.BlockedByAdministrator => $"Access to {requestUri.Host} is blocked by the administrator",
            _ => $"Failed to connect to {requestUri.Host}"
        };
        throw new HackHttpRequestException(result.Error, httpError, message);
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
