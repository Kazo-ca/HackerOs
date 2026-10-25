using System.Text;
using System.Text.Json;

namespace HackerOs.Platform.Core.Keyring;

/// <summary>One decrypted entry of a keyring.</summary>
/// <param name="AppId">Owning app.</param>
/// <param name="Id">Entry id, unique per app.</param>
/// <param name="Label">Optional label.</param>
/// <param name="Secret">The secret.</param>
/// <param name="UpdatedAtUtc">Last write.</param>
internal sealed record KeyringEntryRecord(string AppId, string Id, string? Label, string Secret, DateTimeOffset UpdatedAtUtc);

/// <summary>The sealed envelope of a format 1 <c>login.keyring</c>: KDF parameters, nonce and ciphertext.</summary>
internal sealed record KeyringEnvelope(int Iterations, byte[] Salt, byte[] Nonce, byte[] SealedData);

/// <summary>
/// One key slot of a format 2 keyring (ADR 0168 section 3): the master key wrapped with AES-256-GCM
/// under a key derived from the password (<see cref="KeyringFileFormat.PasswordSlot"/>) or from
/// the PRF output of one passkey (<see cref="KeyringFileFormat.PasskeySlot"/>).
/// </summary>
/// <param name="Kind"><see cref="KeyringFileFormat.PasswordSlot"/> or <see cref="KeyringFileFormat.PasskeySlot"/>.</param>
/// <param name="CredentialId">Base64url id of the passkey; <see langword="null"/> for the password slot.</param>
/// <param name="Salt">PBKDF2 salt of the password slot; <see langword="null"/> for a passkey slot.</param>
/// <param name="Iterations">PBKDF2 iterations of the password slot; 0 for a passkey slot.</param>
/// <param name="Nonce">12-byte nonce of the wrap.</param>
/// <param name="WrappedKey">The wrapped master key (ciphertext and tag).</param>
internal sealed record KeyringSlot(string Kind, string? CredentialId, byte[]? Salt, int Iterations, byte[] Nonce, byte[] WrappedKey);

/// <summary>The envelope of a format 2 keyring: key slots in clear, entries sealed under the master key.</summary>
/// <param name="Slots">Key slots.</param>
/// <param name="Nonce">Nonce of the entries.</param>
/// <param name="SealedData">Entries sealed under the master key.</param>
internal sealed record KeyringEnvelopeV2(IReadOnlyList<KeyringSlot> Slots, byte[] Nonce, byte[] SealedData);

/// <summary>
/// Text format of <c>~/.local/share/keyrings/login.keyring</c>: a small JSON envelope written by
/// hand with <see cref="Utf8JsonWriter"/> so it needs no reflection. Format 1 (ADR 0100 section 2)
/// seals the entries under a key derived from the password; format 2 (ADR 0168 section 3) seals
/// them under a random master key wrapped in one slot per way of unlocking. Only the envelope is
/// readable; the entries are inside the AES-GCM ciphertext.
/// </summary>
internal static class KeyringFileFormat
{
    public const string FormatName = "hackeros-keyring";

    public const int Version = 1;

    public const int VersionWithSlots = 2;

    public const string KdfName = "pbkdf2-sha256";

    public const string CipherName = "aes-256-gcm";

    public const string PasswordSlot = "password";

    public const string PasskeySlot = "passkey";

    /// <summary>Reads the <c>version</c> of a keyring file, or 0 when it is not a keyring.</summary>
    public static int ReadVersion(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            return root.GetProperty("format").GetString() == FormatName ? root.GetProperty("version").GetInt32() : 0;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return 0;
        }
    }

    public static string WriteEnvelope(KeyringEnvelopeV2 envelope)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", FormatName);
            writer.WriteNumber("version", VersionWithSlots);
            writer.WriteString("cipher", CipherName);
            writer.WriteStartArray("slots");
            foreach (KeyringSlot slot in envelope.Slots)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", slot.Kind);
                if (slot.Kind == PasswordSlot)
                {
                    writer.WriteString("kdf", KdfName);
                    writer.WriteNumber("iterations", slot.Iterations);
                    writer.WriteString("salt", Convert.ToBase64String(slot.Salt!));
                }
                else
                {
                    writer.WriteString("credentialId", slot.CredentialId);
                }

                writer.WriteString("nonce", Convert.ToBase64String(slot.Nonce));
                writer.WriteString("wrappedKey", Convert.ToBase64String(slot.WrappedKey));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("nonce", Convert.ToBase64String(envelope.Nonce));
            writer.WriteString("ciphertext", Convert.ToBase64String(envelope.SealedData));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>Parses a format 2 envelope, or returns <see langword="null"/> for another or damaged file.</summary>
    public static KeyringEnvelopeV2? ReadEnvelopeV2(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            if (root.GetProperty("format").GetString() != FormatName
                || root.GetProperty("version").GetInt32() != VersionWithSlots
                || root.GetProperty("cipher").GetString() != CipherName)
            {
                return null;
            }

            List<KeyringSlot> slots = [];
            foreach (JsonElement slot in root.GetProperty("slots").EnumerateArray())
            {
                string kind = slot.GetProperty("kind").GetString()!;
                byte[] nonce = Convert.FromBase64String(slot.GetProperty("nonce").GetString()!);
                byte[] wrapped = Convert.FromBase64String(slot.GetProperty("wrappedKey").GetString()!);
                if (kind == PasswordSlot && slot.GetProperty("kdf").GetString() == KdfName)
                {
                    slots.Add(new KeyringSlot(
                        kind,
                        null,
                        Convert.FromBase64String(slot.GetProperty("salt").GetString()!),
                        slot.GetProperty("iterations").GetInt32(),
                        nonce,
                        wrapped));
                }
                else if (kind == PasskeySlot)
                {
                    slots.Add(new KeyringSlot(kind, slot.GetProperty("credentialId").GetString()!, null, 0, nonce, wrapped));
                }
            }

            return new KeyringEnvelopeV2(
                slots,
                Convert.FromBase64String(root.GetProperty("nonce").GetString()!),
                Convert.FromBase64String(root.GetProperty("ciphertext").GetString()!));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Parses a format 1 envelope, or returns <see langword="null"/> for an unknown or damaged file.</summary>
    public static KeyringEnvelope? ReadEnvelope(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            if (root.GetProperty("format").GetString() != FormatName
                || root.GetProperty("version").GetInt32() != Version
                || root.GetProperty("kdf").GetString() != KdfName
                || root.GetProperty("cipher").GetString() != CipherName)
            {
                return null;
            }

            return new KeyringEnvelope(
                root.GetProperty("iterations").GetInt32(),
                Convert.FromBase64String(root.GetProperty("salt").GetString()!),
                Convert.FromBase64String(root.GetProperty("nonce").GetString()!),
                Convert.FromBase64String(root.GetProperty("ciphertext").GetString()!));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Writes a format 1 envelope (kept for tests of the migration to format 2).</summary>
    public static string WriteEnvelope(KeyringEnvelope envelope)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", FormatName);
            writer.WriteNumber("version", Version);
            writer.WriteString("kdf", KdfName);
            writer.WriteNumber("iterations", envelope.Iterations);
            writer.WriteString("salt", Convert.ToBase64String(envelope.Salt));
            writer.WriteString("cipher", CipherName);
            writer.WriteString("nonce", Convert.ToBase64String(envelope.Nonce));
            writer.WriteString("ciphertext", Convert.ToBase64String(envelope.SealedData));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    public static byte[] WriteEntries(IEnumerable<KeyringEntryRecord> entries)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("entries");
            foreach (KeyringEntryRecord entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("app", entry.AppId);
                writer.WriteString("id", entry.Id);
                if (entry.Label is not null)
                {
                    writer.WriteString("label", entry.Label);
                }

                writer.WriteString("secret", entry.Secret);
                writer.WriteString("updated", entry.UpdatedAtUtc);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Parses decrypted entries, or returns <see langword="null"/> when they are damaged.</summary>
    public static List<KeyringEntryRecord>? ReadEntries(byte[] plaintext)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(plaintext);
            List<KeyringEntryRecord> entries = [];
            foreach (JsonElement entry in document.RootElement.GetProperty("entries").EnumerateArray())
            {
                entries.Add(new KeyringEntryRecord(
                    entry.GetProperty("app").GetString()!,
                    entry.GetProperty("id").GetString()!,
                    entry.TryGetProperty("label", out JsonElement label) ? label.GetString() : null,
                    entry.GetProperty("secret").GetString()!,
                    entry.GetProperty("updated").GetDateTimeOffset()));
            }

            return entries;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }
}
