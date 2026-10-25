using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Sessions;

/// <summary>
/// The checks of ADR 0168 section 1 that need no signature: the client data (type, challenge,
/// origin) and the authenticator data (relying party hash, user presence, counter), parsed in C#
/// as the WebAuthn Level 3 specification describes them.
/// </summary>
internal static class PasskeyResponseChecks
{
    /// <summary>Client data type of a creation.</summary>
    public const string CreateType = "webauthn.create";

    /// <summary>Client data type of an assertion.</summary>
    public const string AssertionType = "webauthn.get";

    private const byte UserPresentFlag = 0x01;
    private const int MinimumAuthenticatorDataLength = 37;

    /// <summary>Gets whether the client data is the one expected for this ceremony.</summary>
    /// <param name="clientDataJson">The client data JSON bytes.</param>
    /// <param name="expectedType"><see cref="CreateType"/> or <see cref="AssertionType"/>.</param>
    /// <param name="challenge">The challenge the OS issued.</param>
    /// <param name="origin">The page origin.</param>
    /// <returns><see langword="null"/> when valid; otherwise the reason.</returns>
    public static string? CheckClientData(byte[] clientDataJson, string expectedType, byte[] challenge, string origin)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(clientDataJson);
            JsonElement root = document.RootElement;
            if (root.GetProperty("type").GetString() != expectedType)
            {
                return "client-data-type";
            }

            byte[]? received = LocalPasskeyCredential.FromBase64Url(root.GetProperty("challenge").GetString());
            if (received is null || !CryptographicOperations.FixedTimeEquals(received, challenge))
            {
                return "challenge";
            }

            if (!string.Equals(root.GetProperty("origin").GetString(), origin, StringComparison.Ordinal))
            {
                return "origin";
            }

            return root.TryGetProperty("crossOrigin", out JsonElement crossOrigin) && crossOrigin.ValueKind == JsonValueKind.True
                ? "cross-origin"
                : null;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "client-data";
        }
    }

    /// <summary>Reads the authenticator data and checks the relying party hash and user presence.</summary>
    /// <param name="authenticatorData">The authenticator data bytes.</param>
    /// <param name="rpId">The relying party id.</param>
    /// <param name="signCount">The signature counter it carries.</param>
    /// <returns><see langword="null"/> when valid; otherwise the reason.</returns>
    public static string? CheckAuthenticatorData(byte[] authenticatorData, string rpId, out uint signCount)
    {
        signCount = 0;
        if (authenticatorData is not { Length: >= MinimumAuthenticatorDataLength })
        {
            return "authenticator-data";
        }

        byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes(rpId));
        if (!CryptographicOperations.FixedTimeEquals(authenticatorData.AsSpan(0, 32), expected))
        {
            return "rp-id";
        }

        if ((authenticatorData[32] & UserPresentFlag) == 0)
        {
            return "user-presence";
        }

        signCount = BinaryPrimitives.ReadUInt32BigEndian(authenticatorData.AsSpan(33, 4));
        return null;
    }

    /// <summary>The bytes an assertion signs: authenticator data followed by SHA-256 of the client data.</summary>
    /// <param name="authenticatorData">The authenticator data.</param>
    /// <param name="clientDataJson">The client data JSON bytes.</param>
    /// <returns>The signed bytes.</returns>
    public static byte[] SignedData(byte[] authenticatorData, byte[] clientDataJson)
    {
        byte[] hash = SHA256.HashData(clientDataJson);
        byte[] data = new byte[authenticatorData.Length + hash.Length];
        authenticatorData.CopyTo(data, 0);
        hash.CopyTo(data, authenticatorData.Length);
        return data;
    }

    /// <summary>
    /// Whether a counter is acceptable (WebAuthn §6.1.1): when either side is non-zero, the new
    /// value must be greater; otherwise the authenticator may have been cloned.
    /// </summary>
    /// <param name="stored">Counter stored with the passkey.</param>
    /// <param name="received">Counter of this assertion.</param>
    /// <returns><see langword="true"/> when the counter is acceptable.</returns>
    public static bool IsCounterAcceptable(uint stored, uint received) =>
        (stored == 0 && received == 0) || received > stored;
}

/// <summary>Converts WebAuthn signatures to the form Web Crypto expects (ADR 0168 section 1).</summary>
public static class PasskeySignatureFormat
{
    /// <summary>
    /// Converts an ECDSA signature from DER (<c>SEQUENCE { r INTEGER, s INTEGER }</c>, as WebAuthn
    /// returns it) to the fixed-size <c>r‖s</c> form of IEEE P1363 that Web Crypto verifies.
    /// </summary>
    /// <param name="der">DER signature.</param>
    /// <param name="fieldSize">Size of r and s in bytes (32 for P-256).</param>
    /// <returns>The <c>r‖s</c> signature, or <see langword="null"/> when the DER is malformed.</returns>
    public static byte[]? DerToP1363(byte[] der, int fieldSize = 32)
    {
        ArgumentNullException.ThrowIfNull(der);
        int position = 0;
        if (!TryRead(der, ref position, 0x30, out int sequenceLength) || position + sequenceLength != der.Length)
        {
            return null;
        }

        byte[] result = new byte[fieldSize * 2];
        for (int part = 0; part < 2; part++)
        {
            if (!TryRead(der, ref position, 0x02, out int length) || position + length > der.Length)
            {
                return null;
            }

            ReadOnlySpan<byte> integer = der.AsSpan(position, length).TrimStart((byte)0);
            if (integer.Length > fieldSize)
            {
                return null;
            }

            integer.CopyTo(result.AsSpan((part * fieldSize) + fieldSize - integer.Length));
            position += length;
        }

        return position == der.Length ? result : null;
    }

    private static bool TryRead(byte[] der, ref int position, byte tag, out int length)
    {
        length = 0;
        if (position + 2 > der.Length || der[position] != tag)
        {
            return false;
        }

        byte first = der[position + 1];
        position += 2;
        if (first < 0x80)
        {
            length = first;
            return true;
        }

        int count = first & 0x7F;
        if (count is 0 or > 2 || position + count > der.Length)
        {
            return false;
        }

        for (int index = 0; index < count; index++)
        {
            length = (length << 8) | der[position++];
        }

        return true;
    }
}
