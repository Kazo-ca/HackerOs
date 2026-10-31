using System.Net.Http.Json;
using HackerOs.Server.Contracts.Identity;

namespace HackerOs.Platform.Core.ServerConnection;

/// <summary>
/// Thin HTTP wrapper over the optional server's account/auth endpoints (ADR 0028). Every member
/// takes only <see cref="HackerOs.Server.Contracts"/> DTOs and primitives — no ASP.NET-specific
/// types. Every host calls the server through it over HTTP (ADR 0130 retired the in-process
/// implementation of ADR 0036). Browser-independent (plain <see cref="HttpClient"/>), unlike
/// the IndexedDB-backed <c>IServerConnectionRepository</c> implementation.
/// </summary>
public interface IAccountClient
{
    /// <summary>Registers a new account on the server (<c>POST api/account</c>).</summary>
    /// <param name="serverBaseUrl">The server's base URL; endpoint paths are resolved relative to it.</param>
    /// <param name="request">The user name, the client-computed password hash and salt, and the first device.</param>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>The new account's id and the id of its registered device; signing in is a separate call.</returns>
    /// <exception cref="ServerConnectionException">The server answered with a non-success status.</exception>
    Task<CreateAccountResponse> CreateAccountAsync(Uri serverBaseUrl, CreateAccountRequest request, CancellationToken cancellationToken = default);

    /// <summary>Signs in to an existing account (<c>POST api/auth/login</c>).</summary>
    /// <param name="serverBaseUrl">The server's base URL; endpoint paths are resolved relative to it.</param>
    /// <param name="request">The user name, password hash and fingerprint of the device signing in.</param>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>The signed-in account's access and refresh tokens.</returns>
    /// <exception cref="ServerConnectionException">The server answered with a non-success status, e.g. wrong credentials.</exception>
    Task<LoginResponse> LoginAsync(Uri serverBaseUrl, LoginRequest request, CancellationToken cancellationToken = default);

    /// <summary>Exchanges a refresh token for a new access token (<c>POST api/auth/refresh</c>).</summary>
    /// <param name="serverBaseUrl">The server's base URL; endpoint paths are resolved relative to it.</param>
    /// <param name="request">The refresh token obtained at sign-in or from the previous refresh.</param>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>The new token pair.</returns>
    /// <exception cref="ServerConnectionException">The server answered with a non-success status, e.g. an expired refresh token.</exception>
    Task<RefreshTokenResponse> RefreshAsync(Uri serverBaseUrl, RefreshTokenRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Links a new device with a one-time pairing code (<c>POST /api/auth/pair</c>, ADR 0191 §6).
    /// </summary>
    /// <param name="serverBaseUrl">The server's base URL.</param>
    /// <param name="request">The code and the new device.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="ServerConnectionException">The server refused the code.</exception>
    Task<LoginResponse> PairAsync(Uri serverBaseUrl, PairDeviceRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This account client cannot pair a device.");
}

/// <summary>Default <see cref="IAccountClient"/> implementation backed by <see cref="HttpClient"/>.</summary>
public sealed class HttpAccountClient(HttpClient httpClient) : IAccountClient
{
    /// <inheritdoc />
    public async Task<CreateAccountResponse> CreateAccountAsync(
        Uri serverBaseUrl, CreateAccountRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            new Uri(serverBaseUrl, "api/account"),
            request,
            IdentityContractsJsonContext.Default.CreateAccountRequest,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(
            IdentityContractsJsonContext.Default.CreateAccountResponse, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<LoginResponse> LoginAsync(
        Uri serverBaseUrl, LoginRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            new Uri(serverBaseUrl, "api/auth/login"),
            request,
            IdentityContractsJsonContext.Default.LoginRequest,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(
            IdentityContractsJsonContext.Default.LoginResponse, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<RefreshTokenResponse> RefreshAsync(
        Uri serverBaseUrl, RefreshTokenRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            new Uri(serverBaseUrl, "api/auth/refresh"),
            request,
            IdentityContractsJsonContext.Default.RefreshTokenRequest,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(
            IdentityContractsJsonContext.Default.RefreshTokenResponse, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<LoginResponse> PairAsync(
        Uri serverBaseUrl, PairDeviceRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            new Uri(serverBaseUrl, "api/auth/pair"),
            request,
            IdentityContractsJsonContext.Default.PairDeviceRequest,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(
            IdentityContractsJsonContext.Default.LoginResponse, cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new ServerConnectionException(
            $"The optional server rejected the request ({(int)response.StatusCode} {response.ReasonPhrase}).", body);
    }
}

/// <summary>Thrown when the optional server rejects an account/auth/proxy request.</summary>
public sealed class ServerConnectionException(string message, string? serverBody = null)
    : Exception(message)
{
    /// <summary>Gets the raw response body the server returned, if any, for diagnostics.</summary>
    public string? ServerBody { get; } = serverBody;
}
