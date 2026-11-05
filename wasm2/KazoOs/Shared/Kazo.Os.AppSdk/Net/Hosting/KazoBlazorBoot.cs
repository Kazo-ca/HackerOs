using System.Text.Json.Serialization;

namespace Kazo.Os.AppSdk.Net.Hosting;

/// <summary>
/// What a Blazor site tells the Kazo OS browser about itself (ADR 0084, section 2): the content of
/// <c>/_framework/blazor.boot.json</c>, the counterpart of a real Blazor WebAssembly site's
/// <c>blazor.boot.json</c>. The browser reads it after the host page loaded
/// <c>_framework/blazor.kazo.js</c>, then asks the OS to start the site's renderer (section 3).
/// </summary>
public sealed record KazoBlazorBootManifest
{
    /// <summary>The path, relative to the site's origin, of the host page's loader script.</summary>
    public const string ScriptPath = "_framework/blazor.kazo.js";

    /// <summary>The path, relative to the site's origin, of the boot manifest.</summary>
    public const string BootPath = "_framework/blazor.boot.json";

    /// <summary>Gets the id of the web app that serves the site (<c>com.example.intranet</c>).</summary>
    [JsonPropertyName("appId")]
    public string AppId { get; init; } = string.Empty;

    /// <summary>Gets the site's display name.</summary>
    [JsonPropertyName("appName")]
    public string AppName { get; init; } = string.Empty;

    /// <summary>Gets the assembly file name of the root component (<c>Intranet.dll</c>), the entry point's.</summary>
    [JsonPropertyName("entryAssembly")]
    public string EntryAssembly { get; init; } = string.Empty;

    /// <summary>Gets the full name of the root component type.</summary>
    [JsonPropertyName("rootComponent")]
    public string RootComponent { get; init; } = string.Empty;
}

/// <summary>JSON source generation for <see cref="KazoBlazorBootManifest"/> (trim-safe on every host).</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(KazoBlazorBootManifest))]
public sealed partial class KazoBlazorBootJsonContext : JsonSerializerContext;
