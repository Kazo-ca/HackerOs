using Kazo.Os.AppSdk;
using System.Text.Json.Serialization;

namespace Kazo.Os.Apps.FileExplorer.FileControl;

/// <summary>
/// Source-generated (trim-safe) serializer for <see cref="FileViewDragEnvelope"/> — the payload written to
/// and read from <c>DataTransfer</c> by <c>FileView.razor.js</c> (<c>FV-006</c>).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FileViewDragEnvelope))]
internal sealed partial class FileViewDragEnvelopeJsonSerializerContext : JsonSerializerContext
{
}

/// <summary>Source-generated serializer for the icon keys of a desktop drag (<see cref="FileViewDesktop"/>, ADR 0160 §3).</summary>
[JsonSerializable(typeof(string[]))]
internal sealed partial class FileViewDesktopJsonContext : JsonSerializerContext
{
}
