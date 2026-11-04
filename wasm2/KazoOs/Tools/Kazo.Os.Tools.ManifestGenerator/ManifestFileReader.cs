using System.Text.Json;
using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Tools.ManifestGenerator;

/// <summary>
/// Reads a manifest file back with the strict schema-v1 deserializer (unknown and duplicate
/// properties rejected), normalizes omitted collections and runs <see cref="AppManifestValidator"/>.
/// </summary>
public static class ManifestFileReader
{
    /// <summary>Reads and validates <paramref name="path"/>.</summary>
    /// <param name="path">Manifest file path.</param>
    /// <param name="manifest">The manifest when valid.</param>
    /// <param name="diagnostics">Problems found; empty when valid.</param>
    /// <returns><see langword="true"/> when the file holds a valid manifest.</returns>
    public static bool TryRead(string path, out AppManifest? manifest, out IReadOnlyList<GeneratorDiagnostic> diagnostics)
    {
        manifest = null;
        try
        {
            manifest = AppManifestJsonSerializer.NormalizeOptionalCollections(AppManifestJsonSerializer.DeserializeStrict(File.ReadAllText(path)));
        }
        catch (Exception exception) when (exception is JsonException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            diagnostics = [new GeneratorDiagnostic(GeneratorCodes.UnreadableManifest, path, exception.Message)];
            return false;
        }

        diagnostics = [.. AppManifestValidator.Validate(manifest).Errors
            .Select(error => new GeneratorDiagnostic(GeneratorCodes.InvalidManifest, path, $"{error.Path}: {error.Message} ({error.Code})"))];
        if (diagnostics.Count > 0)
        {
            manifest = null;
            return false;
        }

        return true;
    }
}
