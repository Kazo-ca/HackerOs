using System.Globalization;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.AppSdk.Settings;

/// <summary>Converts between serialized setting values and the C# types <see cref="KazoSettingsDocument"/> exposes.</summary>
internal static class SettingValueConverter
{
    /// <summary>Converts a valid serialized value to <typeparamref name="T"/>.</summary>
    public static T FromSetting<T>(SettingFieldDeclaration field, string value)
    {
        Type type = typeof(T);
        object result;
        if (type == typeof(string))
        {
            result = value;
        }
        else if (type == typeof(long) && field.ValueType == SettingValueType.Integer)
        {
            result = long.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }
        else if (type == typeof(int) && field.ValueType == SettingValueType.Integer)
        {
            result = checked((int)long.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
        }
        else if (type == typeof(bool) && field.ValueType == SettingValueType.Boolean)
        {
            result = value == "true";
        }
        else if (type.IsEnum && field.ValueType is SettingValueType.Enum or SettingValueType.String)
        {
            result = Enum.GetNames(type)
                .Where(name => Normalize(name) == Normalize(value))
                .Select(name => Enum.Parse(type, name))
                .FirstOrDefault()
                ?? throw new InvalidCastException(
                    $"Value '{value}' of setting '{field.QualifiedKey}' has no matching member in enum {type.Name}.");
        }
        else
        {
            throw new InvalidCastException(
                $"Setting '{field.QualifiedKey}' is declared as {SettingValueCodec.DescribeExpected(field)} and cannot be read as {type.Name}.");
        }

        return (T)result;
    }

    /// <summary>Serializes <paramref name="value"/> for <paramref name="field"/> (validation happens afterwards).</summary>
    public static string ToSetting<T>(SettingFieldDeclaration field, T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        switch (value)
        {
            case string text:
                return text;
            case bool flag:
                return flag ? "true" : "false";
            case long or int or short or byte:
                return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            case Enum member:
                string name = member.ToString();
                return field.AllowedValues?.FirstOrDefault(allowed => Normalize(allowed) == Normalize(name)) ?? name;
            default:
                throw new ArgumentException(
                    $"Type {typeof(T).Name} is not supported for setting '{field.QualifiedKey}'.", nameof(value));
        }
    }

    // "high-contrast", "High_Contrast" and "HighContrast" all name the same choice.
    private static string Normalize(string text) =>
        text.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
}
