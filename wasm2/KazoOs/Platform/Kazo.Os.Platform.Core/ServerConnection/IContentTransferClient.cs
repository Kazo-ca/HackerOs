using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kazo.Os.Server.Contracts.Sync;

namespace Kazo.Os.Platform.Core.ServerConnection;

/// <summary>
/// Thin HTTP wrapper over the optional server's chunked, content-addressed file
/// transfer endpoints (ADR 0030). Same browser-independent, direct-injection-friendly
/// shape as <see cref="ISyncClient"/> — carries only opaque bytes/hashes, never
/// interprets filesystem semantics itself.
/// </summary>
public interface IContentTransferClient
{
    /// <summary>
    /// Starts an upload session for one content blob (<c>POST api/sync/content/upload</c>). Blobs are
    /// addressed by hash, so when the server already holds the content no chunk needs to be sent.
    /// </summary>
    /// <param name="serverBaseUrl">The server's base URL; endpoint paths are resolved relative to it.</param>
    /// <param name="accessToken">The bearer token of the connected account.</param>
    /// <param name="request">The content hash, total size and owner account of the blob.</param>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>The session id, the chunk size and count, or that the content already exists.</returns>
    /// <exception cref="ServerConnectionException">The server answered with a non-success status.</exception>
    Task<InitiateContentUploadResponse> InitiateUploadAsync(
        Uri serverBaseUrl, string accessToken, InitiateContentUploadRequest request, CancellationToken cancellationToken = default);

    /// <summary>Asks which chunks of an upload session the server has received, so an interrupted upload can resume.</summary>
    /// <param name="serverBaseUrl">The server's base URL; endpoint paths are resolved relative to it.</param>
    /// <param name="accessToken">The bearer token of the connected account.</param>
    /// <param name="uploadSessionId">The session returned by <see cref="InitiateUploadAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>The progress of the session.</returns>
    /// <exception cref="ServerConnectionException">The server answered with a non-success status.</exception>
    Task<QueryUploadProgressResponse> QueryUploadProgressAsync(
        Uri serverBaseUrl, string accessToken, string uploadSessionId, CancellationToken cancellationToken = default);

    /// <summary>Sends one chunk of an upload session as <c>application/octet-stream</c> multipart content.</summary>
    /// <param name="serverBaseUrl">The server's base URL; endpoint paths are resolved relative to it.</param>
    /// <param name="accessToken">The bearer token of the connected account.</param>
    /// <param name="uploadSessionId">The session returned by <see cref="InitiateUploadAsync"/>.</param>
    /// <param name="chunkIndex">The zero-based index of the chunk within the blob.</param>
    /// <param name="chunkData">The chunk's bytes.</param>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>A task that completes once the server accepted the chunk.</returns>
    /// <exception cref="ServerConnectionException">The server answered with a non-success status.</exception>
    Task UploadChunkAsync(
        Uri serverBaseUrl, string accessToken, string uploadSessionId, int chunkIndex, byte[] chunkData, CancellationToken cancellationToken = default);

    /// <summary>Prepares the download of one content blob (<c>POST api/sync/content/download</c>).</summary>
    /// <param name="serverBaseUrl">The server's base URL; endpoint paths are resolved relative to it.</param>
    /// <param name="accessToken">The bearer token of the connected account.</param>
    /// <param name="request">The hash of the content and the byte offset to resume from.</param>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>The download session: total and remaining bytes and the chunk size.</returns>
    /// <exception cref="ServerConnectionException">The server answered with a non-success status.</exception>
    Task<InitiateContentDownloadResponse> InitiateDownloadAsync(
        Uri serverBaseUrl, string accessToken, InitiateContentDownloadRequest request, CancellationToken cancellationToken = default);

    /// <summary>Downloads one chunk of a content blob, addressed by its hash.</summary>
    /// <param name="serverBaseUrl">The server's base URL; endpoint paths are resolved relative to it.</param>
    /// <param name="accessToken">The bearer token of the connected account.</param>
    /// <param name="contentHash">The content hash of the blob.</param>
    /// <param name="chunkIndex">The zero-based index of the chunk within the blob.</param>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>The chunk's bytes.</returns>
    /// <exception cref="ServerConnectionException">The server answered with a non-success status.</exception>
    Task<byte[]> DownloadChunkAsync(
        Uri serverBaseUrl, string accessToken, string contentHash, int chunkIndex, CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IContentTransferClient"/> implementation backed by <see cref="HttpClient"/>.</summary>
public sealed class HttpContentTransferClient(HttpClient httpClient) : IContentTransferClient
{
    /// <inheritdoc />
    public async Task<InitiateContentUploadResponse> InitiateUploadAsync(
        Uri serverBaseUrl, string accessToken, InitiateContentUploadRequest request, CancellationToken cancellationToken = default)
    {
        using HttpContent content = JsonContent.Create(request, ContentTransferContractsJsonContext.Default.InitiateContentUploadRequest);
        using HttpResponseMessage response = await SendAsync(
            serverBaseUrl, "api/sync/content/upload", HttpMethod.Post, accessToken, content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(
            ContentTransferContractsJsonContext.Default.InitiateContentUploadResponse, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<QueryUploadProgressResponse> QueryUploadProgressAsync(
        Uri serverBaseUrl, string accessToken, string uploadSessionId, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            serverBaseUrl, $"api/sync/content/upload/{Uri.EscapeDataString(uploadSessionId)}/progress",
            HttpMethod.Get, accessToken, content: null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(
            ContentTransferContractsJsonContext.Default.QueryUploadProgressResponse, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task UploadChunkAsync(
        Uri serverBaseUrl, string accessToken, string uploadSessionId, int chunkIndex, byte[] chunkData, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        using var chunkContent = new ByteArrayContent(chunkData);
        chunkContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(chunkContent, "chunk", "chunk.bin");

        using HttpResponseMessage response = await SendAsync(
            serverBaseUrl, $"api/sync/content/upload/{Uri.EscapeDataString(uploadSessionId)}/chunks/{chunkIndex}",
            HttpMethod.Put, accessToken, form, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<InitiateContentDownloadResponse> InitiateDownloadAsync(
        Uri serverBaseUrl, string accessToken, InitiateContentDownloadRequest request, CancellationToken cancellationToken = default)
    {
        using HttpContent content = JsonContent.Create(request, ContentTransferContractsJsonContext.Default.InitiateContentDownloadRequest);
        using HttpResponseMessage response = await SendAsync(
            serverBaseUrl, "api/sync/content/download", HttpMethod.Post, accessToken, content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(
            ContentTransferContractsJsonContext.Default.InitiateContentDownloadResponse, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<byte[]> DownloadChunkAsync(
        Uri serverBaseUrl, string accessToken, string contentHash, int chunkIndex, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            serverBaseUrl, $"api/sync/content/download/{Uri.EscapeDataString(contentHash)}/chunks/{chunkIndex}",
            HttpMethod.Get, accessToken, content: null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri serverBaseUrl, string relativePath, HttpMethod method, string accessToken, HttpContent? content, CancellationToken cancellationToken)
    {
        using HttpRequestMessage message = new(method, new Uri(serverBaseUrl, relativePath))
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
            $"The content transfer request failed ({(int)response.StatusCode} {response.ReasonPhrase}).", body);
    }
}
