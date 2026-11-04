using System.Globalization;
using System.Text.Json;
using Kazo.Os.AppSdk.WebData;

namespace Kazo.Os.AppSdk.Web.Data;

/// <summary>
/// An IndexedDB key: a number, a string, or an array of keys (ADR 0088 §2). Written from C# values by
/// implicit conversion (<c>store.GetAsync(42)</c>, <c>store.GetAsync("ada")</c>) or with
/// <see cref="Array"/> for a compound key.
/// </summary>
public readonly struct KazoWebDbKey : IEquatable<KazoWebDbKey>
{
    private readonly string? _json;

    private KazoWebDbKey(string json)
    {
        _json = json;
    }

    /// <summary>Gets the key as JSON, as a page's script would see it.</summary>
    public string Json => _json ?? throw new InvalidOperationException("The key is empty.");

    /// <summary>Creates a key from a string.</summary>
    public static implicit operator KazoWebDbKey(string value) =>
        new(JsonSerializer.Serialize(value ?? throw new ArgumentNullException(nameof(value)), KazoWebDbSdkJsonContext.Default.String));

    /// <summary>Creates a key from an integer.</summary>
    public static implicit operator KazoWebDbKey(int value) => new(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Creates a key from an integer.</summary>
    public static implicit operator KazoWebDbKey(long value) => new(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Creates a key from a number (not NaN).</summary>
    public static implicit operator KazoWebDbKey(double value) =>
        double.IsNaN(value) || double.IsInfinity(value)
            ? throw new ArgumentOutOfRangeException(nameof(value), "NaN and infinities are not valid keys.")
            : new(JsonSerializer.Serialize(value, KazoWebDbSdkJsonContext.Default.Double));

    /// <summary>Creates a compound key (an array key), compared element by element.</summary>
    /// <param name="parts">The parts.</param>
    public static KazoWebDbKey Array(params KazoWebDbKey[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return new("[" + string.Join(",", parts.Select(part => part.Json)) + "]");
    }

    /// <summary>Reads a key from its JSON (what the database returns).</summary>
    /// <param name="json">The JSON of a number, string or array.</param>
    /// <exception cref="ArgumentException">The JSON is not a valid key.</exception>
    public static KazoWebDbKey FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind is not (JsonValueKind.Number or JsonValueKind.String or JsonValueKind.Array))
        {
            throw new ArgumentException($"'{json}' is not a valid key.", nameof(json));
        }

        return new(json);
    }

    /// <summary>Returns the key as an integer.</summary>
    /// <exception cref="InvalidOperationException">The key is not a number.</exception>
    public long AsInt64()
    {
        using JsonDocument document = JsonDocument.Parse(Json);
        return document.RootElement.ValueKind == JsonValueKind.Number
            ? (long)document.RootElement.GetDouble()
            : throw new InvalidOperationException($"The key {Json} is not a number.");
    }

    /// <summary>Returns the key as a string.</summary>
    /// <exception cref="InvalidOperationException">The key is not a string.</exception>
    public string AsString()
    {
        using JsonDocument document = JsonDocument.Parse(Json);
        return document.RootElement.ValueKind == JsonValueKind.String
            ? document.RootElement.GetString()!
            : throw new InvalidOperationException($"The key {Json} is not a string.");
    }

    /// <inheritdoc />
    public bool Equals(KazoWebDbKey other) => string.Equals(_json, other._json, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is KazoWebDbKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _json is null ? 0 : StringComparer.Ordinal.GetHashCode(_json);

    /// <inheritdoc />
    public override string ToString() => _json ?? string.Empty;

    /// <summary>Compares two keys' JSON.</summary>
    public static bool operator ==(KazoWebDbKey left, KazoWebDbKey right) => left.Equals(right);

    /// <summary>Compares two keys' JSON.</summary>
    public static bool operator !=(KazoWebDbKey left, KazoWebDbKey right) => !left.Equals(right);
}

/// <summary>An <c>IDBKeyRange</c>: the keys between two bounds, in IndexedDB's key order.</summary>
public sealed class KazoWebDbKeyRange
{
    private KazoWebDbKeyRange(KazoWebDbRange range)
    {
        Range = range;
    }

    internal KazoWebDbRange Range { get; }

    /// <summary>Only <paramref name="key"/> (<c>IDBKeyRange.only</c>).</summary>
    public static KazoWebDbKeyRange Only(KazoWebDbKey key) => new(new KazoWebDbRange(key.Json, key.Json));

    /// <summary>Keys from <paramref name="lower"/> up (<c>IDBKeyRange.lowerBound</c>).</summary>
    public static KazoWebDbKeyRange LowerBound(KazoWebDbKey lower, bool open = false) => new(new KazoWebDbRange(lower.Json, null, open));

    /// <summary>Keys up to <paramref name="upper"/> (<c>IDBKeyRange.upperBound</c>).</summary>
    public static KazoWebDbKeyRange UpperBound(KazoWebDbKey upper, bool open = false) => new(new KazoWebDbRange(null, upper.Json, false, open));

    /// <summary>Keys between two bounds (<c>IDBKeyRange.bound</c>).</summary>
    public static KazoWebDbKeyRange Bound(KazoWebDbKey lower, KazoWebDbKey upper, bool lowerOpen = false, bool upperOpen = false) =>
        new(new KazoWebDbRange(lower.Json, upper.Json, lowerOpen, upperOpen));
}

/// <summary>JSON of keys.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
[System.Text.Json.Serialization.JsonSerializable(typeof(double))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
[System.Text.Json.Serialization.JsonSerializable(typeof(long))]
[System.Text.Json.Serialization.JsonSerializable(typeof(bool))]
internal sealed partial class KazoWebDbSdkJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
