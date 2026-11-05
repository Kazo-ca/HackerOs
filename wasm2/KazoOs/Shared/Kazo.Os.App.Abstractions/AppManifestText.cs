using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Kazo.Os.App.Abstractions;

/// <summary>
/// The translated texts of one app manifest in one language (ADR 0104, section 4): what the
/// launcher, the taskbar, Settings and <c>help</c> show instead of the English
/// <see cref="AppManifest.Name"/>, <see cref="AppManifest.Description"/> and
/// <see cref="TerminalCommandManifest.Usage"/>.
/// </summary>
/// <param name="Name">Translated display name.</param>
/// <param name="Description">Translated description.</param>
/// <param name="TerminalUsage">Translated usage line of a terminal command, or <see langword="null"/>.</param>
public sealed record AppManifestText(string Name, string Description, string? TerminalUsage = null);

/// <summary>
/// The strict JSON format of a manifest localization file, <c>localization/&lt;culture&gt;.json</c>,
/// which the build generates from <c>Resources/AppManifest.&lt;culture&gt;.resx</c> and an entry of
/// <see cref="AppManifest.Localizations"/> points to:
/// <c>{"name":"Calculatrice","description":"…","terminalUsage":"…"}</c> (camelCase,
/// <c>terminalUsage</c> optional, unknown and duplicate properties rejected).
/// </summary>
public static class AppManifestTextJsonSerializer
{
    /// <summary>Writes the canonical file content, terminated by a newline.</summary>
    /// <param name="text">The translated texts.</param>
    public static string SerializeCanonical(AppManifestText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            Write(writer, text);
        }

        return Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>Reads a file strictly.</summary>
    /// <param name="json">File content.</param>
    /// <exception cref="JsonException">The content is malformed, has an unknown or duplicate property, or lacks a text.</exception>
    public static AppManifestText DeserializeStrict(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using JsonDocument document = JsonDocument.Parse(json);
        return Read(document.RootElement, "localization");
    }

    /// <summary>Writes <paramref name="text"/> as one JSON object.</summary>
    internal static void Write(Utf8JsonWriter writer, AppManifestText text)
    {
        writer.WriteStartObject();
        writer.WriteString("name", text.Name);
        writer.WriteString("description", text.Description);
        if (text.TerminalUsage is { } usage)
        {
            writer.WriteString("terminalUsage", usage);
        }

        writer.WriteEndObject();
    }

    /// <summary>Reads one JSON object written by <see cref="Write"/>.</summary>
    internal static AppManifestText Read(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"'{path}' must be an object.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new JsonException($"Duplicate property '{property.Name}' at '{path}'.");
            }

            if (property.Name is not ("name" or "description" or "terminalUsage"))
            {
                throw new JsonException($"Unknown property '{property.Name}' at '{path}'.");
            }
        }

        string name = RequireText(element, "name", path);
        string description = RequireText(element, "description", path);
        string? usage = element.TryGetProperty("terminalUsage", out _) ? RequireText(element, "terminalUsage", path) : null;
        return new AppManifestText(name, description, usage);
    }

    private static string RequireText(JsonElement element, string property, string path) =>
        element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new JsonException($"'{path}.{property}' must be a non-empty string.");
}

/// <summary>Applies translated manifest texts (ADR 0104, section 4).</summary>
public static class AppManifestLocalization
{
    /// <summary>
    /// Returns the texts of <paramref name="localized"/> that match <paramref name="culture"/>: the
    /// exact culture first, then its parents (<c>fr-CA</c>, then <c>fr</c>); <see langword="null"/>
    /// when none match, and the English manifest texts then apply.
    /// </summary>
    /// <param name="localized">Translations keyed by culture name (<c>fr</c>).</param>
    /// <param name="culture">The language wanted.</param>
    public static AppManifestText? Find(IReadOnlyDictionary<string, AppManifestText>? localized, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (localized is null || localized.Count == 0)
        {
            return null;
        }

        for (CultureInfo current = culture; !current.Equals(CultureInfo.InvariantCulture); current = current.Parent)
        {
            if (localized.TryGetValue(current.Name, out AppManifestText? text))
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns <paramref name="manifest"/> with its name, description and terminal usage in
    /// <paramref name="culture"/> when a translation exists, otherwise the manifest itself. The id,
    /// the terminal command name and everything else are never translated.
    /// </summary>
    /// <param name="manifest">The English manifest.</param>
    /// <param name="localized">Translations keyed by culture name.</param>
    /// <param name="culture">The language wanted.</param>
    public static AppManifest Apply(AppManifest manifest, IReadOnlyDictionary<string, AppManifestText>? localized, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (Find(localized, culture) is not { } text)
        {
            return manifest;
        }

        return manifest with
        {
            Name = text.Name,
            Description = text.Description,
            Terminal = manifest.Terminal is { } terminal && text.TerminalUsage is { } usage
                ? terminal with { Usage = usage }
                : manifest.Terminal
        };
    }
}
