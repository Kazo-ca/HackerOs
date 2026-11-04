using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;

namespace Kazo.Os.AppSdk.Web.Routing;

/// <summary>
/// Route values (<c>controller</c>, <c>action</c>, <c>id</c>...), like ASP.NET Core's
/// <c>RouteValueDictionary</c>: keys compare without case.
/// </summary>
public sealed class HackRouteValueDictionary : Dictionary<string, object?>
{
    /// <summary>Initializes an empty dictionary.</summary>
    public HackRouteValueDictionary()
        : base(StringComparer.OrdinalIgnoreCase)
    {
    }

    /// <summary>Initializes a copy of <paramref name="values"/>.</summary>
    /// <param name="values">Values to copy.</param>
    public HackRouteValueDictionary(IEnumerable<KeyValuePair<string, object?>> values)
        : base(StringComparer.OrdinalIgnoreCase)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (KeyValuePair<string, object?> value in values)
        {
            this[value.Key] = value.Value;
        }
    }

    /// <summary>
    /// Reads route values from an object: a dictionary, or the public properties of an anonymous
    /// object (<c>new { id = 5 }</c>), as ASP.NET does for <c>RedirectToAction("Details", new { id })</c>.
    /// </summary>
    /// <typeparam name="TValues">The static type of the values; the trimmer keeps its public properties.</typeparam>
    /// <param name="values">The values, or <see langword="null"/>.</param>
    /// <returns>A new dictionary.</returns>
    /// <exception cref="ArgumentException">The values are typed <see cref="object"/>: their properties cannot be read safely.</exception>
    /// <remarks>
    /// Properties are read from <typeparamref name="TValues"/>, not from the run-time type: an anonymous
    /// object passed directly infers its own type and keeps its properties after trimming.
    /// </remarks>
    public static HackRouteValueDictionary FromObject<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TValues>(TValues? values)
    {
        switch (values)
        {
            case null:
                return [];
            case HackRouteValueDictionary existing:
                return new HackRouteValueDictionary(existing);
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                return new HackRouteValueDictionary(pairs);
            case IEnumerable<KeyValuePair<string, string>> strings:
                return new HackRouteValueDictionary(strings.Select(pair => new KeyValuePair<string, object?>(pair.Key, pair.Value)));
        }

        if (typeof(TValues) == typeof(object))
        {
            throw new ArgumentException("Pass route values as an anonymous object (new { id = 5 }) or a dictionary, not as an object.", nameof(values));
        }

        HackRouteValueDictionary result = [];
        foreach (PropertyInfo property in typeof(TValues).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0 && property.CanRead)
            {
                result[property.Name] = property.GetValue(values);
            }
        }

        return result;
    }

    /// <summary>Formats a route value the way URLs carry it (invariant culture, <c>true</c>/<c>false</c> in lower case).</summary>
    /// <param name="value">The value.</param>
    /// <returns>The text, or <see langword="null"/> for <see langword="null"/>.</returns>
    public static string? FormatValue(object? value) => value switch
    {
        null => null,
        string text => text,
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    /// <summary>Gets a value as text (see <see cref="FormatValue"/>), or <see langword="null"/>.</summary>
    /// <param name="key">Route value name.</param>
    public string? GetString(string key) => TryGetValue(key, out object? value) ? FormatValue(value) : null;
}
