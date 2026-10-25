using System.Text.Json.Serialization;

namespace HackerOs.AppSdk.Icons;

/// <summary>
/// Compact on-disk shape for one icon record in the embedded per-library JSON resources. Field
/// names are single letters because these files are embedded verbatim in the assembly and are
/// generated in bulk by the icon-import tooling (see <c>doc/wasm/dev/old/icon-library.md</c>).
/// </summary>
/// <param name="N">Lookup name.</param>
/// <param name="V">SVG viewBox.</param>
/// <param name="B">Inner SVG markup.</param>
/// <param name="S">1 when stroke-based, 0 when filled.</param>
/// <param name="G">Optional sub-style/group (e.g. Font Awesome's solid/regular/brands), or null.</param>
/// <param name="T">Human-readable display title.</param>
internal sealed record IconRecord(string N, string V, string B, int S, string? G, string? T);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(List<IconRecord>))]
internal sealed partial class IconCatalogJsonSerializerContext : JsonSerializerContext
{
}
