using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HackerOs.Server.LocalServer;

/// <summary>
/// One-time pairing codes of an installed server (ADR 0191 §6, <c>doc/en/wasm/dev/installable-server.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// A code is written by <c>HackerOs.Server pair &lt;account&gt;</c>, a second process started on the
/// server's machine while the server itself may be running as a service. The two processes share
/// files, not memory, so each code is one small file in <see cref="Root"/>: its name is the SHA-256
/// of the code (the code itself is never stored), its content the account name and the expiry.
/// This also needs no new database table, which the migration-free SQLite bootstrap of the server
/// could not add to an existing database.
/// </para>
/// <para>
/// Redeeming moves the file away before reading it, so two requests with the same code cannot both
/// succeed. Failed attempts are counted per process: past <see cref="MaxFailedAttemptsPerMinute"/>
/// in one minute every attempt is refused until the minute ends, which keeps the 39-bit codes out
/// of reach of guessing during their ten minutes of life.
/// </para>
/// </remarks>
public sealed class PairingCodeStore
{
    /// <summary>Configuration key of the folder that holds the codes (default <c>pairing</c>).</summary>
    public const string RootSettingKey = "HackerOs:Pairing:Root";

    /// <summary>How long a code stays valid.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>Failed attempts tolerated per minute before every attempt is refused.</summary>
    public const int MaxFailedAttemptsPerMinute = 10;

    // No 0/O, 1/I/L or U, so a code read aloud or typed is unambiguous.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";
    private const int CodeLength = 8;

    private readonly TimeProvider _time;
    private readonly Lock _attemptsLock = new();
    private DateTimeOffset _attemptsWindowStart = DateTimeOffset.MinValue;
    private int _failedAttempts;

    /// <summary>Creates the store over <paramref name="root"/>.</summary>
    /// <param name="root">Folder of the code files; created when a code is written.</param>
    /// <param name="time">Clock (tests use a fake one).</param>
    public PairingCodeStore(string root, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Absolute folder of the code files.</summary>
    public string Root { get; }

    /// <summary>Creates a code for <paramref name="accountName"/>, valid for <see cref="Lifetime"/>.</summary>
    /// <param name="accountName">Server account the paired device will belong to.</param>
    /// <returns>The code, formatted <c>XXXX-XXXX</c>.</returns>
    public string Create(string accountName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        Directory.CreateDirectory(Root);
        PurgeExpired();

        string code = RandomNumberGenerator.GetString(Alphabet, CodeLength);
        var entry = new PairingEntry(accountName, _time.GetUtcNow().Add(Lifetime));
        File.WriteAllText(PathOf(code), JsonSerializer.Serialize(entry, PairingJsonContext.Default.PairingEntry));
        return $"{code[..4]}-{code[4..]}";
    }

    /// <summary>
    /// Consumes <paramref name="code"/>: returns its account name and deletes it, or returns
    /// <see langword="null"/> when the code is unknown, expired or already used.
    /// </summary>
    /// <param name="code">The code as typed (case, spaces and dashes are ignored).</param>
    /// <exception cref="PairingRateLimitedException">Too many failed attempts in the current minute.</exception>
    public string? Redeem(string? code)
    {
        ThrowIfRateLimited();
        string? normalized = Normalize(code);
        string? account = normalized is null ? null : TakeAccount(normalized);
        if (account is null)
        {
            RecordFailure();
        }

        return account;
    }

    /// <summary>Upper-cases a typed code and removes separators; <see langword="null"/> when it cannot be one.</summary>
    /// <param name="code">The code as typed.</param>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var builder = new StringBuilder(CodeLength);
        foreach (char character in code)
        {
            if (character is '-' or ' ')
            {
                continue;
            }

            char upper = char.ToUpperInvariant(character);
            if (!Alphabet.Contains(upper, StringComparison.Ordinal) || builder.Length == CodeLength)
            {
                return null;
            }

            builder.Append(upper);
        }

        return builder.Length == CodeLength ? builder.ToString() : null;
    }

    private string? TakeAccount(string normalizedCode)
    {
        string path = PathOf(normalizedCode);
        string claimed = path + "." + Guid.NewGuid().ToString("N") + ".claimed";
        try
        {
            // The move is the single-use guarantee: only one request can move the file.
            File.Move(path, claimed);
        }
        catch (IOException)
        {
            return null;
        }

        try
        {
            PairingEntry? entry = JsonSerializer.Deserialize(File.ReadAllText(claimed), PairingJsonContext.Default.PairingEntry);
            return entry is not null && entry.ExpiresUtc > _time.GetUtcNow() ? entry.AccountName : null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            File.Delete(claimed);
        }
    }

    private void PurgeExpired()
    {
        DateTimeOffset now = _time.GetUtcNow();
        foreach (string file in Directory.EnumerateFiles(Root, "*.code"))
        {
            try
            {
                PairingEntry? entry = JsonSerializer.Deserialize(File.ReadAllText(file), PairingJsonContext.Default.PairingEntry);
                if (entry is null || entry.ExpiresUtc <= now)
                {
                    File.Delete(file);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                // A file another process is redeeming, or a damaged one: the next purge retries.
            }
        }
    }

    private void ThrowIfRateLimited()
    {
        lock (_attemptsLock)
        {
            ResetWindowIfOver();
            if (_failedAttempts >= MaxFailedAttemptsPerMinute)
            {
                throw new PairingRateLimitedException();
            }
        }
    }

    private void RecordFailure()
    {
        lock (_attemptsLock)
        {
            ResetWindowIfOver();
            _failedAttempts++;
        }
    }

    private void ResetWindowIfOver()
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (now - _attemptsWindowStart >= TimeSpan.FromMinutes(1))
        {
            _attemptsWindowStart = now;
            _failedAttempts = 0;
        }
    }

    private string PathOf(string normalizedCode) =>
        Path.Combine(Root, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCode))) + ".code");
}

/// <summary>Too many wrong pairing codes in the current minute; the caller answers <c>429</c>.</summary>
public sealed class PairingRateLimitedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PairingRateLimitedException()
        : base("Too many wrong pairing codes; try again in a minute.")
    {
    }
}

/// <summary>Content of one code file.</summary>
/// <param name="AccountName">Server account the code links a device to.</param>
/// <param name="ExpiresUtc">When the code stops being valid.</param>
internal sealed record PairingEntry(string AccountName, DateTimeOffset ExpiresUtc);

[System.Text.Json.Serialization.JsonSerializable(typeof(PairingEntry))]
internal sealed partial class PairingJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
