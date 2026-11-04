using System.Net.Http.Json;
using Kazo.Os.Server.Contracts.Hosting;

namespace Kazo.Os.Platform.Core.ServerConnection;

/// <summary>
/// Reads the host discovery of ADR 0134 and ADR 0137: which origin served the OS, and whether a
/// Kazo OS server answers at a given address.
/// </summary>
public interface IHostInfoClient
{
    /// <summary>
    /// Reads <c>hackeros-host.json</c> next to the OS's own <c>index.html</c>. Returns the
    /// address of the origin when a Kazo OS server served the OS, and <see langword="null"/> on a
    /// static host, offline, on any error, or when the host has no own origin (tests, a server
    /// process). Never throws except on cancellation.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<Uri?> FindServingServerAsync(CancellationToken cancellationToken = default);

    /// <summary>Calls <c>GET /api/hackeros/host-info</c> on a server.</summary>
    /// <param name="serverBaseUrl">The server's base URL.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>What the server says about itself.</returns>
    /// <exception cref="ServerConnectionException">The address answered, but not as a Kazo OS server.</exception>
    /// <exception cref="HttpRequestException">The server could not be reached.</exception>
    Task<HostInfoResponse> GetHostInfoAsync(Uri serverBaseUrl, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IHostInfoClient"/> over <see cref="HttpClient"/>. The discovery reads a file that
/// exists on every host (a static file on a static host, an endpoint on <c>Kazo.Os.Server</c>),
/// so a standalone PWA never logs a failed request at boot.
/// </summary>
/// <param name="httpClient">The browser's HTTP client.</param>
/// <param name="ownOrigin">The base address the OS was loaded from, or <see langword="null"/> when there is none.</param>
public sealed class HttpHostInfoClient(HttpClient httpClient, Uri? ownOrigin) : IHostInfoClient
{
    /// <summary>The discovery is a hint: a slow answer is treated as "no server".</summary>
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task<Uri?> FindServingServerAsync(CancellationToken cancellationToken = default)
    {
        if (ownOrigin is null)
        {
            return null;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DiscoveryTimeout);
        try
        {
            HostInfoResponse? info = await httpClient.GetFromJsonAsync(
                new Uri(ownOrigin, HostInfoRoutes.DiscoveryFile),
                HostingContractsJsonContext.Default.HostInfoResponse,
                timeout.Token).ConfigureAwait(false);
            return info is { IsServer: true } ? ownOrigin : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Offline, a 404 on a host that ships no file, malformed JSON, the timeout: no server.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<HostInfoResponse> GetHostInfoAsync(Uri serverBaseUrl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverBaseUrl);
        using HttpResponseMessage response = await httpClient.GetAsync(
            new Uri(serverBaseUrl, HostInfoRoutes.ApiPath.TrimStart('/')), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ServerConnectionException(
                $"{serverBaseUrl} answered {(int)response.StatusCode} to {HostInfoRoutes.ApiPath}: not a Kazo OS server.");
        }

        HostInfoResponse? info;
        try
        {
            info = await response.Content.ReadFromJsonAsync(
                HostingContractsJsonContext.Default.HostInfoResponse, cancellationToken).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new ServerConnectionException($"{serverBaseUrl} is not a Kazo OS server: {exception.Message}");
        }

        return info is { IsServer: true }
            ? info
            : throw new ServerConnectionException($"{serverBaseUrl} is not a Kazo OS server.");
    }
}
