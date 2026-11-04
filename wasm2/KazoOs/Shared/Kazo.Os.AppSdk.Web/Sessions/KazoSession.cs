using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Kazo.Os.AppSdk.Web.Sessions;

/// <summary>
/// The data a site keeps for one visitor between requests (ASP.NET's <c>ISession</c>, ADR 0076 §6),
/// found again through a cookie. It lives in the memory of the site's process (plan §4.1, answer 15):
/// stopping the site, or the web worker, forgets every session, as an IIS app pool recycle does.
/// </summary>
public sealed class KazoSession
{
    private readonly KazoSessionStore _store;
    private readonly Dictionary<string, byte[]> _values;

    internal KazoSession(KazoSessionStore store, string id, Dictionary<string, byte[]> values, bool isNew)
    {
        _store = store;
        Id = id;
        _values = values;
        IsNew = isNew;
    }

    /// <summary>Gets the session identifier (the cookie value).</summary>
    public string Id { get; }

    /// <summary>Gets whether the session was created by this request (no valid cookie came with it).</summary>
    public bool IsNew { get; }

    /// <summary>Gets whether this request changed the session.</summary>
    public bool IsModified { get; private set; }

    /// <summary>Gets the keys.</summary>
    public IEnumerable<string> Keys
    {
        get
        {
            lock (_values)
            {
                return [.. _values.Keys];
            }
        }
    }

    /// <summary>Gets a value.</summary>
    /// <param name="key">Key.</param>
    /// <param name="value">The bytes, when present.</param>
    public bool TryGetValue(string key, out byte[]? value)
    {
        lock (_values)
        {
            bool found = _values.TryGetValue(key, out byte[]? stored);
            value = stored is null ? null : [.. stored];
            return found;
        }
    }

    /// <summary>Sets a value.</summary>
    /// <param name="key">Key.</param>
    /// <param name="value">The bytes.</param>
    public void Set(string key, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_values)
        {
            _values[key] = [.. value];
            IsModified = true;
        }
    }

    /// <summary>Removes a value.</summary>
    /// <param name="key">Key.</param>
    public void Remove(string key)
    {
        lock (_values)
        {
            IsModified |= _values.Remove(key);
        }
    }

    /// <summary>Removes every value (the cookie stays; the session is empty).</summary>
    public void Clear()
    {
        lock (_values)
        {
            _values.Clear();
            IsModified = true;
        }
    }

    /// <summary>Gets a string value, or <see langword="null"/>.</summary>
    /// <param name="key">Key.</param>
    public string? GetString(string key) => TryGetValue(key, out byte[]? bytes) && bytes is not null ? Encoding.UTF8.GetString(bytes) : null;

    /// <summary>Sets a string value (stored as UTF-8).</summary>
    /// <param name="key">Key.</param>
    /// <param name="value">Value.</param>
    public void SetString(string key, string value) => Set(key, Encoding.UTF8.GetBytes(value ?? string.Empty));

    /// <summary>Gets a 32-bit integer value, or <see langword="null"/> (stored big-endian, like ASP.NET).</summary>
    /// <param name="key">Key.</param>
    public int? GetInt32(string key) =>
        TryGetValue(key, out byte[]? bytes) && bytes is { Length: 4 } ? BinaryPrimitives.ReadInt32BigEndian(bytes) : null;

    /// <summary>Sets a 32-bit integer value.</summary>
    /// <param name="key">Key.</param>
    /// <param name="value">Value.</param>
    public void SetInt32(string key, int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        Set(key, bytes);
    }

    /// <summary>
    /// Keeps the session for the next requests: a new session is stored only once written (as in
    /// ASP.NET, a visitor who never writes gets no session); an existing one gets its idle timer reset.
    /// </summary>
    internal void Commit()
    {
        if (IsModified || !IsNew)
        {
            _store.Touch(Id, _values);
        }
    }
}

/// <summary>Options of <c>UseCookieSession</c>.</summary>
public sealed class KazoSessionOptions
{
    /// <summary>Gets or sets the cookie name (<c>.AspNetCore.Session</c> in ASP.NET; <c>.KazoSession</c> here).</summary>
    public string CookieName { get; set; } = ".KazoSession";

    /// <summary>Gets or sets how long an unused session is kept (20 minutes, as ASP.NET).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>Gets or sets the cookie path.</summary>
    public string CookiePath { get; set; } = "/";
}

/// <summary>The sessions of a site, in its process's memory (a singleton of the site's services).</summary>
public sealed class KazoSessionStore
{
    private readonly ConcurrentDictionary<string, Entry> _sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    /// <summary>Initializes a store.</summary>
    /// <param name="time">Clock for idle timeouts.</param>
    public KazoSessionStore(TimeProvider time) => _time = time ?? TimeProvider.System;

    /// <summary>Gets the number of live sessions.</summary>
    public int Count
    {
        get
        {
            Sweep(TimeSpan.MaxValue);
            return _sessions.Count;
        }
    }

    /// <summary>Gets or sets the idle timeout (set by <c>UseCookieSession</c>).</summary>
    internal TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>Loads the session of <paramref name="cookieValue"/>, or starts a new one.</summary>
    /// <param name="cookieValue">The cookie sent by the client, or <see langword="null"/>.</param>
    internal KazoSession Load(string? cookieValue)
    {
        Sweep(IdleTimeout);
        if (cookieValue is not null && _sessions.TryGetValue(cookieValue, out Entry? entry))
        {
            entry.LastAccess = _time.GetUtcNow();
            return new KazoSession(this, cookieValue, entry.Values, isNew: false);
        }

        // 256 random bits, URL-safe: a session id cannot be guessed.
        string id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new KazoSession(this, id, new Dictionary<string, byte[]>(StringComparer.Ordinal), isNew: true);
    }

    /// <summary>Records a session's values and use.</summary>
    internal void Touch(string id, Dictionary<string, byte[]> values)
    {
        _sessions.AddOrUpdate(
            id,
            _ => new Entry(values) { LastAccess = _time.GetUtcNow() },
            (_, existing) =>
            {
                existing.LastAccess = _time.GetUtcNow();
                return existing;
            });
    }

    private void Sweep(TimeSpan idleTimeout)
    {
        if (idleTimeout == TimeSpan.MaxValue)
        {
            idleTimeout = IdleTimeout;
        }

        DateTimeOffset now = _time.GetUtcNow();
        foreach (KeyValuePair<string, Entry> session in _sessions)
        {
            if (now - session.Value.LastAccess > idleTimeout)
            {
                _sessions.TryRemove(session.Key, out _);
            }
        }
    }

    private sealed class Entry(Dictionary<string, byte[]> values)
    {
        public Dictionary<string, byte[]> Values { get; } = values;

        public DateTimeOffset LastAccess { get; set; }
    }
}
