using System.Diagnostics.CodeAnalysis;
using HackerOs.Platform.Core.Sessions;
using HackerOs.Simulation.Abstractions.Sessions;
using Microsoft.JSInterop;

namespace HackerOs.Ecosystem.Passkeys;

/// <summary>
/// <see cref="IPasskeyAuthenticator"/> on the page's WebAuthn (ADR 0168): <c>webAuthn.js</c> calls
/// <c>navigator.credentials</c> and verifies signatures with Web Crypto, which .NET in WebAssembly
/// lacks. Buffers travel as base64url strings.
/// </summary>
internal sealed class BrowserPasskeyAuthenticator(IJSRuntime runtime) : IPasskeyAuthenticator, IAsyncDisposable
{
    private const string ModulePath = "./_content/HackerOs.Infrastructure.Browser/webAuthn.js";

    private readonly SemaphoreSlim _moduleGate = new(1, 1);
    private IJSObjectReference? _module;

    /// <inheritdoc />
    public async ValueTask<PasskeyEnvironment> GetEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsBrowser())
        {
            return new PasskeyEnvironment(false, string.Empty, string.Empty);
        }

        IJSObjectReference module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        EnvironmentDto environment = await module.InvokeAsync<EnvironmentDto>("getEnvironment", cancellationToken).ConfigureAwait(false);
        return new PasskeyEnvironment(environment.IsSupported, environment.Origin ?? string.Empty, environment.RpId ?? string.Empty);
    }

    /// <inheritdoc />
    public async ValueTask<PasskeyRegistrationResponse> CreateAsync(PasskeyCreationOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        IJSObjectReference module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        CreationDto dto = new(
            Encode(options.Challenge),
            options.RpId,
            options.RpName,
            Encode(options.UserHandle),
            options.UserName,
            options.UserDisplayName,
            [.. options.ExcludeCredentialIds.Select(Encode)],
            Encode(options.PrfSalt));
        RegistrationDto result = await CallAsync<RegistrationDto>(module, "create", dto, cancellationToken).ConfigureAwait(false);
        return new PasskeyRegistrationResponse(
            Decode(result.CredentialId),
            Decode(result.ClientDataJson),
            Decode(result.AuthenticatorData),
            LocalPasskeyCredential.FromBase64Url(result.PublicKey),
            result.Algorithm,
            result.PrfEnabled,
            LocalPasskeyCredential.FromBase64Url(result.PrfOutput));
    }

    /// <inheritdoc />
    public async ValueTask<PasskeyAssertionResponse> GetAsync(PasskeyRequestOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        IJSObjectReference module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        RequestDto dto = new(
            Encode(options.Challenge),
            options.RpId,
            [.. options.AllowCredentials.Select(static allowed => new AllowedDto(Encode(allowed.Id), allowed.PrfSalt is null ? null : Encode(allowed.PrfSalt)))]);
        AssertionDto result = await CallAsync<AssertionDto>(module, "get", dto, cancellationToken).ConfigureAwait(false);
        return new PasskeyAssertionResponse(
            Decode(result.CredentialId),
            Decode(result.ClientDataJson),
            Decode(result.AuthenticatorData),
            Decode(result.Signature),
            LocalPasskeyCredential.FromBase64Url(result.PrfOutput));
    }

    /// <inheritdoc />
    public async ValueTask<bool> VerifySignatureAsync(int algorithm, byte[] publicKey, byte[] data, byte[] signature, CancellationToken cancellationToken = default)
    {
        byte[]? webCryptoSignature = algorithm == LocalPasskeyCredential.Es256 ? PasskeySignatureFormat.DerToP1363(signature) : signature;
        if (webCryptoSignature is null)
        {
            return false;
        }

        IJSObjectReference module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        return await module.InvokeAsync<bool>(
            "verify", cancellationToken, algorithm, Encode(publicKey), Encode(data), Encode(webCryptoSignature)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // Browser shutdown can disconnect JS before DI disposal.
            }
        }

        _moduleGate.Dispose();
    }

    /// <summary>Maps the WebAuthn errors of <c>webAuthn.js</c> to the exceptions of <see cref="IPasskeyAuthenticator"/>.</summary>
    private static async ValueTask<T> CallAsync<[DynamicallyAccessedMembers(
        DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        IJSObjectReference module, string function, object argument, CancellationToken cancellationToken)
    {
        try
        {
            return await module.InvokeAsync<T>(function, cancellationToken, argument).ConfigureAwait(false);
        }
        catch (JSException exception) when (exception.Message.Contains("webauthn:NotAllowedError", StringComparison.Ordinal)
            || exception.Message.Contains("webauthn:AbortError", StringComparison.Ordinal))
        {
            throw new OperationCanceledException("The passkey prompt was closed.", exception);
        }
        catch (JSException exception) when (exception.Message.Contains("webauthn:InvalidStateError", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("This authenticator already holds a passkey of this account.", exception);
        }
        catch (JSException exception) when (exception.Message.Contains("webauthn:NotSupportedError", StringComparison.Ordinal)
            || exception.Message.Contains("webauthn:SecurityError", StringComparison.Ordinal))
        {
            throw new NotSupportedException("This browser cannot use a passkey on this page.", exception);
        }
    }

    private async ValueTask<IJSObjectReference> GetModuleAsync(CancellationToken cancellationToken)
    {
        if (_module is not null)
        {
            return _module;
        }

        await _moduleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _module ??= await runtime.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath).ConfigureAwait(false);
            return _module;
        }
        finally
        {
            _moduleGate.Release();
        }
    }

    private static string Encode(byte[] bytes) => LocalPasskeyCredential.ToBase64Url(bytes);

    private static byte[] Decode(string? text) =>
        LocalPasskeyCredential.FromBase64Url(text) ?? throw new InvalidOperationException("The browser returned an empty WebAuthn field.");

    private sealed record EnvironmentDto(bool IsSupported, string? Origin, string? RpId);

    private sealed record CreationDto(
        string Challenge, string RpId, string RpName, string UserHandle, string UserName, string UserDisplayName,
        string[] ExcludeCredentialIds, string PrfSalt);

    private sealed record RegistrationDto(
        string? CredentialId, string? ClientDataJson, string? AuthenticatorData, string? PublicKey, int Algorithm,
        bool PrfEnabled, string? PrfOutput);

    private sealed record AllowedDto(string Id, string? PrfSalt);

    private sealed record RequestDto(string Challenge, string RpId, AllowedDto[] AllowCredentials);

    private sealed record AssertionDto(
        string? CredentialId, string? ClientDataJson, string? AuthenticatorData, string? Signature, string? PrfOutput);
}
