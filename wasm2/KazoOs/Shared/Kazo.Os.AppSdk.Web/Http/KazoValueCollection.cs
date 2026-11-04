using System.Collections;

namespace Kazo.Os.AppSdk.Web.Http;

/// <summary>
/// Decoded name/value fields of a query string or a form, like ASP.NET Core's <c>IQueryCollection</c>
/// and <c>IFormCollection</c>: names compare without case and a name may repeat (<c>?tag=a&amp;tag=b</c>).
/// </summary>
public sealed class KazoValueCollection : IEnumerable<KeyValuePair<string, IReadOnlyList<string>>>
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];

    /// <summary>An empty collection.</summary>
    public static KazoValueCollection Empty { get; } = new();

    /// <summary>Gets the number of distinct names.</summary>
    public int Count => _values.Count;

    /// <summary>Gets the distinct names, in order of first appearance.</summary>
    public IReadOnlyList<string> Keys => _order;

    /// <summary>Gets the values of <paramref name="name"/> joined with <c>","</c>, or <see langword="null"/>.</summary>
    /// <param name="name">Field name.</param>
    public string? this[string name] => _values.TryGetValue(name, out List<string>? values) ? string.Join(",", values) : null;

    /// <summary>Gets every value of <paramref name="name"/>; empty when absent.</summary>
    /// <param name="name">Field name.</param>
    public IReadOnlyList<string> GetValues(string name) =>
        _values.TryGetValue(name, out List<string>? values) ? values : [];

    /// <summary>Gets whether <paramref name="name"/> is present (even with an empty value).</summary>
    /// <param name="name">Field name.</param>
    public bool ContainsKey(string name) => _values.ContainsKey(name);

    /// <summary>Parses <c>a=1&amp;b=x+y</c> (with or without a leading <c>?</c>), decoding <c>+</c> and <c>%XX</c>.</summary>
    /// <param name="encoded">The encoded text.</param>
    /// <returns>The fields.</returns>
    public static KazoValueCollection Parse(string? encoded)
    {
        KazoValueCollection collection = new();
        if (string.IsNullOrEmpty(encoded))
        {
            return collection;
        }

        foreach (string pair in encoded.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            string name = Decode(equals < 0 ? pair : pair[..equals]);
            string value = equals < 0 ? string.Empty : Decode(pair[(equals + 1)..]);
            if (name.Length > 0)
            {
                collection.Add(name, value);
            }
        }

        return collection;
    }

    /// <summary>Builds a collection from fields (tests, and forms read by the SDK).</summary>
    /// <param name="fields">Name/value pairs; a repeated name keeps every value.</param>
    public static KazoValueCollection From(IEnumerable<KeyValuePair<string, string>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        KazoValueCollection collection = new();
        foreach (KeyValuePair<string, string> field in fields)
        {
            collection.Add(field.Key, field.Value);
        }

        return collection;
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

    private void Add(string name, string value)
    {
        if (!_values.TryGetValue(name, out List<string>? values))
        {
            values = [];
            _values[name] = values;
            _order.Add(name);
        }

        values.Add(value);
    }

    private static string Decode(string text)
    {
        try
        {
            return Uri.UnescapeDataString(text.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return text;
        }
    }
}
