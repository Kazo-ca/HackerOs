using System.Collections;

namespace Kazo.Os.AppSdk.Web.Http;

/// <summary>
/// HTTP headers of a request or a response, like ASP.NET Core's <c>IHeaderDictionary</c>: names
/// compare without case and a name may carry several values (<c>Set-Cookie</c>, <c>Allow</c>...).
/// </summary>
public sealed class KazoHeaderDictionary : IEnumerable<KeyValuePair<string, IReadOnlyList<string>>>
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];

    /// <summary>Gets the number of header names.</summary>
    public int Count => _values.Count;

    /// <summary>
    /// Gets the values of <paramref name="name"/> joined with <c>", "</c> (<see langword="null"/> when
    /// absent), or replaces them; setting <see langword="null"/> removes the header.
    /// </summary>
    /// <param name="name">Header name.</param>
    public string? this[string name]
    {
        get => _values.TryGetValue(name, out List<string>? values) ? string.Join(", ", values) : null;
        set
        {
            Remove(name);
            if (value is not null)
            {
                Append(name, value);
            }
        }
    }

    /// <summary>Adds a value to <paramref name="name"/>, keeping the others.</summary>
    /// <param name="name">Header name.</param>
    /// <param name="value">Value to add.</param>
    public void Append(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        if (!_values.TryGetValue(name, out List<string>? values))
        {
            values = [];
            _values[name] = values;
            _order.Add(name);
        }

        values.Add(value);
    }

    /// <summary>Gets every value of <paramref name="name"/>, in order; empty when absent.</summary>
    /// <param name="name">Header name.</param>
    public IReadOnlyList<string> GetValues(string name) =>
        _values.TryGetValue(name, out List<string>? values) ? values : [];

    /// <summary>Gets whether <paramref name="name"/> is present.</summary>
    /// <param name="name">Header name.</param>
    public bool ContainsKey(string name) => _values.ContainsKey(name);

    /// <summary>Removes every value of <paramref name="name"/>.</summary>
    /// <param name="name">Header name.</param>
    /// <returns><see langword="true"/> when the header was present.</returns>
    public bool Remove(string name)
    {
        if (!_values.Remove(name))
        {
            return false;
        }

        _order.RemoveAll(existing => existing.Equals(name, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    /// <summary>Removes every header.</summary>
    public void Clear()
    {
        _values.Clear();
        _order.Clear();
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, IReadOnlyList<string>>> GetEnumerator()
    {
        foreach (string name in _order)
        {
            yield return new(name, _values[name]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
