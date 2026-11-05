namespace Kazo.Os.AppSdk.Web.Mvc;

/// <summary>One bound value of <see cref="KazoModelStateDictionary"/>: what the client sent and its errors.</summary>
public sealed class KazoModelStateEntry
{
    /// <summary>Gets the text the client sent, or <see langword="null"/>.</summary>
    public string? AttemptedValue { get; internal set; }

    /// <summary>Gets the error messages.</summary>
    public List<string> Errors { get; } = [];
}

/// <summary>
/// Binding and validation state of an action's arguments, like ASP.NET MVC's <c>ModelStateDictionary</c>
/// (ADR 0076 §4): a conversion failure (<c>id=abc</c> for an <c>int</c>) or a failed <c>DataAnnotations</c>
/// attribute adds an error under the field's key. Keys compare without case.
/// </summary>
public sealed class KazoModelStateDictionary
{
    private readonly Dictionary<string, KazoModelStateEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets whether no error was recorded.</summary>
    public bool IsValid => ErrorCount == 0;

    /// <summary>Gets the number of errors.</summary>
    public int ErrorCount => _entries.Values.Sum(entry => entry.Errors.Count);

    /// <summary>Gets the keys with an entry.</summary>
    public IEnumerable<string> Keys => _entries.Keys;

    /// <summary>Gets the entry of <paramref name="key"/>, or <see langword="null"/>.</summary>
    /// <param name="key">Field key (<c>id</c>, <c>Title</c>, <c>note.Title</c>...).</param>
    public KazoModelStateEntry? this[string key] => _entries.GetValueOrDefault(key);

    /// <summary>Records an error under <paramref name="key"/>.</summary>
    /// <param name="key">Field key; empty for a model-level error.</param>
    /// <param name="errorMessage">The message shown to the user.</param>
    public void AddModelError(string key, string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(key);
        GetOrAdd(key).Errors.Add(errorMessage);
    }

    /// <summary>Records the text the client sent for <paramref name="key"/> (views show it back).</summary>
    /// <param name="key">Field key.</param>
    /// <param name="attemptedValue">The raw text.</param>
    public void SetAttemptedValue(string key, string? attemptedValue) => GetOrAdd(key).AttemptedValue = attemptedValue;

    /// <summary>Gets the entry of <paramref name="key"/>.</summary>
    /// <param name="key">Field key.</param>
    /// <param name="entry">The entry.</param>
    public bool TryGetValue(string key, out KazoModelStateEntry? entry) => _entries.TryGetValue(key, out entry);

    /// <summary>Removes the entry of <paramref name="key"/> (to accept a field after all).</summary>
    /// <param name="key">Field key.</param>
    public bool Remove(string key) => _entries.Remove(key);

    /// <summary>Removes every entry.</summary>
    public void Clear() => _entries.Clear();

    /// <summary>Gets the errors per key, as <c>application/problem+json</c> carries them (<c>errors</c>).</summary>
    public Dictionary<string, string[]> ToErrorDictionary() =>
        _entries.Where(entry => entry.Value.Errors.Count > 0)
            .ToDictionary(entry => entry.Key, entry => entry.Value.Errors.ToArray(), StringComparer.OrdinalIgnoreCase);

    private KazoModelStateEntry GetOrAdd(string key)
    {
        if (!_entries.TryGetValue(key, out KazoModelStateEntry? entry))
        {
            entry = new KazoModelStateEntry();
            _entries[key] = entry;
        }

        return entry;
    }
}
