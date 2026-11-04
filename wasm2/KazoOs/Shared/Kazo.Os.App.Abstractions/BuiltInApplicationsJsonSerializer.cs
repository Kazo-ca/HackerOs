using System.Text;
using System.Text.Json;

namespace Kazo.Os.App.Abstractions;

/// <summary>
/// One application shipped with a host build, as listed in <c>BuiltInApplications.json</c>
/// (ADR 0047 §3).
/// </summary>
/// <param name="IsSystemApp">
/// Whether the host declared the app a system app (<c>IsSystemApp</c> metadata on its
/// <c>ProjectReference</c>, ADR 0048). An app can never set this itself.
/// </param>
/// <param name="Manifest">The app's schema-version-1 manifest.</param>
/// <param name="Image">
/// The assemblies the app maps and their sizes, measured by the host build (ADR 0068, image layer);
/// <see langword="null"/> when unknown (an older document, or an app installed at run time).
/// </param>
/// <param name="Localized">
/// The translated name, description and usage of the app, keyed by culture (<c>fr</c>), read by the
/// host build from the files its <see cref="AppManifest.Localizations"/> point to (ADR 0104,
/// section 4); <see langword="null"/> or empty when the app is only in English.
/// </param>
public sealed record BuiltInApplication(
    bool IsSystemApp,
    AppManifest Manifest,
    AppImage? Image = null,
    IReadOnlyDictionary<string, AppManifestText>? Localized = null);

/// <summary>
/// One assembly an app maps (ADR 0068, image layer): its file name, its exact size, and whether other
/// apps share it.
/// </summary>
/// <param name="Name">The file name, e.g. <c>Kazo.Os.Apps.TextEditor.dll</c>.</param>
/// <param name="Bytes">The size of the built assembly, in bytes.</param>
/// <param name="Shared">
/// <see langword="false"/> for the app's own assembly (<c>VmExe</c>); <see langword="true"/> for a
/// library it references (the SDK, a component library), counted once machine-wide.
/// </param>
public sealed record AppImageAssembly(string Name, long Bytes, bool Shared);

/// <summary>
/// The executable image of a built-in app (ADR 0068): its own assembly and the shared assemblies it
/// references, outside the .NET runtime and framework, which belong to the OS itself.
/// </summary>
/// <param name="Assemblies">Every assembly, sorted by name.</param>
public sealed record AppImage(IReadOnlyList<AppImageAssembly> Assemblies)
{
    /// <summary>Gets an image with no assembly.</summary>
    public static AppImage Empty { get; } = new([]);

    /// <summary>Gets the bytes of the app's own assemblies (<c>VmExe</c>).</summary>
    public long OwnBytes => Assemblies.Where(static assembly => !assembly.Shared).Sum(static assembly => assembly.Bytes);

    /// <summary>Gets the bytes of the shared assemblies it maps.</summary>
    public long SharedBytes => Assemblies.Where(static assembly => assembly.Shared).Sum(static assembly => assembly.Bytes);

    /// <summary>Gets everything the app maps (<c>RssFile</c>).</summary>
    public long TotalBytes => OwnBytes + SharedBytes;
}

/// <summary>
/// Strict JSON format of <c>BuiltInApplications.json</c>, the catalog a host build aggregates from
/// the generated manifests of every referenced app and embeds as the
/// <c>Kazo.Os.BuiltInApplications.json</c> resource:
/// <c>{"schemaVersion":1,"applications":[{"isSystemApp":true,"manifest":{...},"image":{...}}]}</c>.
/// The optional <c>image</c> lists the app's assemblies and their sizes (ADR 0068):
/// <c>{"assemblies":[{"name":"X.dll","bytes":1234,"shared":false}]}</c>.
/// The optional <c>localized</c> object holds the app's translated texts by culture (ADR 0104):
/// <c>{"fr":{"name":"Calculatrice","description":"…"}}</c>.
/// Same conventions as <see cref="AppManifestJsonSerializer"/>: camelCase, unknown and duplicate
/// properties rejected, canonical output (applications sorted by id, canonical manifests).
/// </summary>
public static class BuiltInApplicationsJsonSerializer
{
    /// <summary>Logical name of the embedded resource holding the document in a host assembly.</summary>
    public const string ResourceName = "Kazo.Os.BuiltInApplications.json";

    private const int CurrentSchemaVersion = 1;

    /// <summary>Writes the canonical document, terminated by a newline.</summary>
    /// <param name="applications">Applications to list; order is irrelevant.</param>
    /// <returns>Canonical JSON text.</returns>
    public static string SerializeCanonical(IEnumerable<BuiltInApplication> applications)
    {
        ArgumentNullException.ThrowIfNull(applications);

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
            writer.WriteStartArray("applications");
            foreach (BuiltInApplication application in applications.OrderBy(item => item.Manifest.Id, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteBoolean("isSystemApp", application.IsSystemApp);
                writer.WritePropertyName("manifest");
                writer.WriteRawValue(AppManifestJsonSerializer.SerializeCanonical(application.Manifest).TrimEnd('\n'));
                if (application.Image is { } image)
                {
                    WriteImage(writer, image);
                }

                if (application.Localized is { Count: > 0 } localized)
                {
                    writer.WriteStartObject("localized");
                    foreach (KeyValuePair<string, AppManifestText> text in localized.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(text.Key);
                        AppManifestTextJsonSerializer.Write(writer, text.Value);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>
    /// Reads a document strictly. Each manifest goes through
    /// <see cref="AppManifestJsonSerializer.DeserializeStrict"/>; omitted optional collections are
    /// normalized to empty lists. Manifests are not semantically validated here: callers build an
    /// <c>AppCatalog</c>, which validates them.
    /// </summary>
    /// <param name="json">Document text.</param>
    /// <returns>The applications in document order.</returns>
    /// <exception cref="JsonException">The document is malformed, has an unknown/duplicate property or an unsupported version.</exception>
    public static IReadOnlyList<BuiltInApplication> DeserializeStrict(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        RequireObject(root, "document", ["schemaVersion", "applications"]);

        if (!root.TryGetProperty("schemaVersion", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out int schemaVersion)
            || schemaVersion != CurrentSchemaVersion)
        {
            throw new JsonException("BuiltInApplications.json must declare schemaVersion 1.");
        }

        if (!root.TryGetProperty("applications", out JsonElement applications) || applications.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("BuiltInApplications.json must contain an 'applications' array.");
        }

        List<BuiltInApplication> result = [];
        int index = 0;
        foreach (JsonElement entry in applications.EnumerateArray())
        {
            string path = $"applications[{index++}]";
            RequireObject(entry, path, ["isSystemApp", "manifest", "image", "localized"]);
            if (!entry.TryGetProperty("isSystemApp", out JsonElement isSystem)
                || isSystem.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new JsonException($"'{path}.isSystemApp' must be a boolean.");
            }

            if (!entry.TryGetProperty("manifest", out JsonElement manifest) || manifest.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException($"'{path}.manifest' must be an object.");
            }

            AppImage? image = entry.TryGetProperty("image", out JsonElement imageElement)
                ? ReadImage(imageElement, $"{path}.image")
                : null;
            IReadOnlyDictionary<string, AppManifestText>? localized = entry.TryGetProperty("localized", out JsonElement localizedElement)
                ? ReadLocalized(localizedElement, $"{path}.localized")
                : null;
            result.Add(new BuiltInApplication(
                isSystem.GetBoolean(),
                AppManifestJsonSerializer.NormalizeOptionalCollections(AppManifestJsonSerializer.DeserializeStrict(manifest.GetRawText())),
                image,
                localized));
        }

        return result;
    }

    private static Dictionary<string, AppManifestText> ReadLocalized(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"'{path}' must be an object.");
        }

        Dictionary<string, AppManifestText> result = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) || !result.TryAdd(property.Name, AppManifestTextJsonSerializer.Read(property.Value, $"{path}.{property.Name}")))
            {
                throw new JsonException($"Invalid or duplicate culture '{property.Name}' at '{path}'.");
            }
        }

        return result;
    }

    private static void WriteImage(Utf8JsonWriter writer, AppImage image)
    {
        writer.WriteStartObject("image");
        writer.WriteStartArray("assemblies");
        foreach (AppImageAssembly assembly in image.Assemblies.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("name", assembly.Name);
            writer.WriteNumber("bytes", assembly.Bytes);
            writer.WriteBoolean("shared", assembly.Shared);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static AppImage ReadImage(JsonElement element, string path)
    {
        RequireObject(element, path, ["assemblies"]);
        if (!element.TryGetProperty("assemblies", out JsonElement assemblies) || assemblies.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"'{path}.assemblies' must be an array.");
        }

        List<AppImageAssembly> result = [];
        int index = 0;
        foreach (JsonElement item in assemblies.EnumerateArray())
        {
            string itemPath = $"{path}.assemblies[{index++}]";
            RequireObject(item, itemPath, ["name", "bytes", "shared"]);
            if (!item.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(name.GetString()))
            {
                throw new JsonException($"'{itemPath}.name' must be a non-empty string.");
            }

            if (!item.TryGetProperty("bytes", out JsonElement bytes) || bytes.ValueKind != JsonValueKind.Number
                || !bytes.TryGetInt64(out long size) || size < 0)
            {
                throw new JsonException($"'{itemPath}.bytes' must be a non-negative integer.");
            }

            if (!item.TryGetProperty("shared", out JsonElement shared) || shared.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new JsonException($"'{itemPath}.shared' must be a boolean.");
            }

            result.Add(new AppImageAssembly(name.GetString()!, size, shared.GetBoolean()));
        }

        return new AppImage(result);
    }

    private static void RequireObject(JsonElement element, string path, IReadOnlyCollection<string> allowed)
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

            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new JsonException($"Unknown property '{property.Name}' at '{path}'.");
            }
        }
    }
}
