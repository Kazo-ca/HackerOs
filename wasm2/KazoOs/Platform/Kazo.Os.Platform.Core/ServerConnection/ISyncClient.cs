using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kazo.Os.Server.Contracts.Sync;

namespace Kazo.Os.Platform.Core.ServerConnection;

/// <summary>
/// Thin HTTP wrapper over the optional server's sync endpoints (ADR 0025/0029). Same
/// browser-independent, direct-injection-friendly shape as <see cref="IAccountClient"/>/
/// <see cref="IProxyClient"/>.
/// </summary>
public interface ISyncClient
{
    /// <summary>Fetches the next page of records of one sync domain changed after the cursor (<c>POST api/sync/pull</c>).</summary>
    /// <param name="serverBaseUrl">The server's base URL; endpoint paths are resolved relative to it.</param>
    /// <param name="accessToken">The bearer token of the connected account.</param>
    /// <param name="request">The domain, the cursor of the previous pull (or none for a full refresh) and the page size.</param>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>The records, the next cursor and whether more remain.</returns>
    /// <exception cref="ServerConnectionException">The server answered with a non-success status.</exception>
    Task<PullResponse> PullAsync(
        Uri serverBaseUrl, string accessToken, PullRequest request, CancellationToken cancellationToken = default);

    /// <summary>Sends a batch of locally modified records of one sync domain (<c>POST api/sync/push</c>).</summary>
    /// <param name="serverBaseUrl">The server's base URL; endpoint paths are resolved relative to it.</param>
    /// <param name="accessToken">The bearer token of the connected account.</param>
    /// <param name="request">The records, with an idempotency key so a retried push is not applied twice.</param>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>The outcome, the number of records stored and the conflicts that need an explicit resolution.</returns>
    /// <exception cref="ServerConnectionException">The server answered with a non-success status.</exception>
    Task<PushResponse> PushAsync(
        Uri serverBaseUrl, string accessToken, PushRequest request, CancellationToken cancellationToken = default);

    /// <summary>Submits the resolution chosen for one conflicting record (<c>POST api/sync/conflicts/resolve</c>).</summary>
    /// <param name="serverBaseUrl">The server's base URL; endpoint paths are resolved relative to it.</param>
    /// <param name="accessToken">The bearer token of the connected account.</param>
    /// <param name="request">The record, the chosen strategy and, for a merge, the merged payload.</param>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>Whether the server accepted it (it refuses resolutions that would weaken security policy) and the final revision.</returns>
    /// <exception cref="ServerConnectionException">The server answered with a non-success status.</exception>
    Task<ResolveSyncConflictResponse> ResolveConflictAsync(
        Uri serverBaseUrl, string accessToken, ResolveSyncConflictRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="ISyncClient"/> implementation backed by <see cref="HttpClient"/>.</summary>
public sealed class HttpSyncClient(HttpClient httpClient) : ISyncClient
{
    /// <inheritdoc />
    public async Task<PullResponse> PullAsync(
        Uri serverBaseUrl, string accessToken, PullRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            serverBaseUrl, "api/sync/pull", accessToken, JsonContent.Create(request, SyncContractsJsonContext.Default.PullRequest),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(
            SyncContractsJsonContext.Default.PullResponse, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<PushResponse> PushAsync(
        Uri serverBaseUrl, string accessToken, PushRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            serverBaseUrl, "api/sync/push", accessToken, JsonContent.Create(request, SyncContractsJsonContext.Default.PushRequest),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(
            SyncContractsJsonContext.Default.PushResponse, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<ResolveSyncConflictResponse> ResolveConflictAsync(
        Uri serverBaseUrl, string accessToken, ResolveSyncConflictRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            serverBaseUrl, "api/sync/conflicts/resolve", accessToken,
            JsonContent.Create(request, SyncContractsJsonContext.Default.ResolveSyncConflictRequest),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(
            SyncContractsJsonContext.Default.ResolveSyncConflictResponse, cancellationToken).ConfigureAwait(false))!;
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri serverBaseUrl, string relativePath, string accessToken, HttpContent content, CancellationToken cancellationToken)
    {
        using HttpRequestMessage message = new(HttpMethod.Post, new Uri(serverBaseUrl, relativePath))
        {
            Content = content
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new ServerConnectionException(
            $"The sync request failed ({(int)response.StatusCode} {response.ReasonPhrase}).", body);
    }
}
