using System.Reflection;

namespace Kazo.Os.App.Abstractions;

/// <summary>
/// Loads the canonical embedded manifest JSON Schema so no consumer maintains a second copy.
/// </summary>
public static class ManifestSchemaResource
{
    private const string CurrentResourceName = "Kazo.Os.App.Abstractions.Schema.manifest.schema.v1.json";

    /// <summary>Gets the raw JSON Schema Draft 2020-12 document text for the current manifest schema version.</summary>
    public static string LoadCurrentSchemaJson()
    {
        Assembly assembly = typeof(ManifestSchemaResource).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(CurrentResourceName)
            ?? throw new InvalidOperationException($"Embedded manifest schema resource '{CurrentResourceName}' was not found.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
