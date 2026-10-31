using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HackerOs.App.Abstractions;

namespace HackerOs.Tools.ManifestGenerator;

/// <summary>
/// The static web resources an app project ships (ADR 0220 §1): read from the build's
/// <c>staticwebassets.build.json</c> and added to the <b>package's</b> manifest, so an installed app
/// gets the same <c>_content/&lt;assembly&gt;/</c> files Blazor gives an app compiled into the OS.
/// </summary>
/// <remarks>
/// Kept: the project's own resources (origin <c>Discovered</c> or <c>Computed</c>, role
/// <c>Primary</c>, not publish-only): its <c>wwwroot/</c>, a component's collocated
/// <c>.razor.js</c>, the <c>&lt;assembly&gt;.bundle.scp.css</c> bundle of its scoped CSS. Left out:
/// referenced packages and projects (the SDK comes with the OS) and the
/// <c>&lt;assembly&gt;.styles.css</c> application bundle, which only a host serves.
/// </remarks>
public static partial class PackagedWebAssets
{
    private static readonly HashSet<string> OwnSourceTypes = new(StringComparer.Ordinal) { "Discovered", "Computed" };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".svg", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".bmp", ".ico",
    };

    /// <summary>One resource of the project: the path it is served at and the file it comes from.</summary>
    /// <param name="Path">Path under <c>_content/&lt;assembly&gt;/</c>, <c>/</c> separators, no fingerprint.</param>
    /// <param name="SourceFile">Full path of the file on disk.</param>
    public sealed record WebAsset(string Path, string SourceFile);

    /// <summary>The resources to pack, and those left out with the reason.</summary>
    /// <param name="Assets">The resources to pack, ordered by path.</param>
    /// <param name="Skipped">One line per resource left out (a path the manifest would refuse).</param>
    public sealed record ReadResult(IReadOnlyList<WebAsset> Assets, IReadOnlyList<string> Skipped);

    /// <summary>Reads the project's own resources from a build manifest.</summary>
    /// <param name="buildManifestJson">The text of <c>staticwebassets.build.json</c>.</param>
    /// <returns>The resources, one per served path.</returns>
    /// <exception cref="JsonException">The text is not a static web asset manifest.</exception>
    public static ReadResult Read(string buildManifestJson)
    {
        ArgumentNullException.ThrowIfNull(buildManifestJson);
        using JsonDocument document = JsonDocument.Parse(buildManifestJson);
        JsonElement root = document.RootElement;
        string source = ReadString(root, "Source")
            ?? throw new JsonException("The static web asset manifest has no 'Source'.");
        if (!root.TryGetProperty("Assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The static web asset manifest has no 'Assets' array.");
        }

        SortedDictionary<string, WebAsset> kept = new(StringComparer.Ordinal);
        List<string> skipped = [];
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (!IsOwnPrimaryResource(asset, source) || ReadString(asset, "Identity") is not { } file)
            {
                continue;
            }

            string path = ServedPath(ReadString(asset, "RelativePath") ?? string.Empty);
            if (!IsPackagePath(path))
            {
                skipped.Add($"'{path}' ({file}) is not a path a manifest accepts (no '..', '#', '?', backslash or leading '/').");
                continue;
            }

            kept.TryAdd(path, new WebAsset(path, file));
        }

        return new ReadResult([.. kept.Values], skipped);
    }

    /// <summary>
    /// Returns <paramref name="manifest"/> with every resource it does not declare yet added to its
    /// <c>Assets</c>, with its SHA-256 and a kind from its extension. A declared resource keeps its
    /// declaration.
    /// </summary>
    /// <param name="manifest">The generated manifest.</param>
    /// <param name="assets">The project's resources.</param>
    /// <param name="readFile">Reads a resource's bytes from its source file.</param>
    /// <returns>The package's manifest.</returns>
    public static AppManifest Complete(AppManifest manifest, IReadOnlyList<WebAsset> assets, Func<string, byte[]> readFile)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(readFile);
        IReadOnlyList<AssetManifest> declared = manifest.Assets ?? [];
        HashSet<string> known = new(declared.Select(static asset => asset.Path), StringComparer.Ordinal);
        List<AssetManifest> added = [];
        foreach (WebAsset asset in assets)
        {
            if (known.Add(asset.Path))
            {
                string sha256 = Convert.ToHexStringLower(SHA256.HashData(readFile(asset.SourceFile)));
                added.Add(new AssetManifest(asset.Path, KindOf(asset.Path), sha256));
            }
        }

        return added.Count == 0 ? manifest : manifest with { Assets = [.. declared, .. added] };
    }

    /// <summary>Gets the declared kind of a resource from its extension.</summary>
    /// <param name="path">Its served path.</param>
    public static AssetKind KindOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string extension = System.IO.Path.GetExtension(path);
        return extension.ToLowerInvariant() switch
        {
            ".css" => AssetKind.Css,
            ".js" or ".mjs" => AssetKind.JavaScriptModule,
            _ when ImageExtensions.Contains(extension) => AssetKind.Image,
            _ => AssetKind.Data,
        };
    }

    /// <summary>
    /// Removes the fingerprint pattern of a build path (<c>app#[.{fingerprint}]?.js</c> → <c>app.js</c>):
    /// an installed app's files are served at their plain address.
    /// </summary>
    /// <param name="relativePath">The asset's <c>RelativePath</c> in the build manifest.</param>
    public static string ServedPath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return FingerprintPattern().Replace(relativePath, string.Empty).Replace('\\', '/').TrimStart('/');
    }

    private static bool IsOwnPrimaryResource(JsonElement asset, string source) =>
        string.Equals(ReadString(asset, "SourceId"), source, StringComparison.Ordinal)
        && OwnSourceTypes.Contains(ReadString(asset, "SourceType") ?? string.Empty)
        && string.Equals(ReadString(asset, "AssetRole"), "Primary", StringComparison.Ordinal)
        && !string.Equals(ReadString(asset, "AssetKind"), "Publish", StringComparison.Ordinal)
        && !string.Equals(ReadString(asset, "AssetTraitValue"), "ApplicationBundle", StringComparison.Ordinal);

    // The rules of AppManifestValidator for a package-relative path, checked before the manifest is
    // written so one odd file name is left out instead of breaking the whole package.
    private static bool IsPackagePath(string path) =>
        path.Length > 0
        && !path.StartsWith('/')
        && !path.Contains('\\')
        && !path.Contains("..", StringComparison.Ordinal)
        && !path.Contains('?')
        && !path.Contains('#')
        && !path.Contains("://", StringComparison.Ordinal);

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // "#[.{fingerprint}]", followed by "?" (optional in the URL) or "!" (required): never in a plain path.
    [GeneratedRegex(@"#\[[^\]]*\][?!]?", RegexOptions.CultureInvariant)]
    private static partial Regex FingerprintPattern();
}
