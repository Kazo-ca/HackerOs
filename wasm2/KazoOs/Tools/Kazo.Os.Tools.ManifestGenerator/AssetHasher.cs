using System.Security.Cryptography;
using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Tools.ManifestGenerator;

/// <summary>
/// Computes the SHA-256 of every asset an app declares (ADR 0098 §5), so a developer never types a
/// hash: <c>AppDefinition.Assets</c> names the file, the build writes its hash into
/// <c>assets[].sha256</c>, and the platform checks the bytes against it at first read.
/// </summary>
/// <remarks>
/// An asset path is relative to the app project's <c>wwwroot/</c> folder, which Blazor serves as
/// <c>_content/&lt;assembly name&gt;/</c>: the same file the platform later fetches.
/// </remarks>
public static class AssetHasher
{
    /// <summary>Placeholder hash a definition may declare; the build replaces it (any value is replaced).</summary>
    public const string Placeholder = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// Returns <paramref name="manifest"/> with each asset's hash computed from
    /// <c><paramref name="assetRoot"/>/&lt;path&gt;</c>. A missing file adds one <c>HOSAPP0023</c>
    /// and keeps the declared hash.
    /// </summary>
    /// <param name="manifest">The produced manifest.</param>
    /// <param name="assetRoot">The app project's <c>wwwroot/</c> folder.</param>
    /// <param name="subject">The definition class, for diagnostics.</param>
    /// <param name="diagnostics">Receives one diagnostic per missing file.</param>
    /// <returns>The manifest with computed hashes.</returns>
    public static AppManifest Apply(AppManifest manifest, string assetRoot, string subject, List<GeneratorDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(assetRoot);
        ArgumentNullException.ThrowIfNull(diagnostics);

        AssetManifest[] assets = new AssetManifest[manifest.Assets.Count];
        for (int index = 0; index < assets.Length; index++)
        {
            AssetManifest asset = manifest.Assets[index];
            string? file = Resolve(assetRoot, asset.Path);
            if (file is null || !File.Exists(file))
            {
                diagnostics.Add(new(GeneratorCodes.AssetMissing, subject,
                    $"Asset '{asset.Path}' was not found under '{assetRoot}'. Declared asset paths are relative to the project's wwwroot/ folder."));
                assets[index] = asset;
                continue;
            }

            assets[index] = asset with { Sha256 = HashFile(file) };
        }

        return manifest with { Assets = assets };
    }

    /// <summary>Gets the lowercase hexadecimal SHA-256 of a file's bytes.</summary>
    /// <param name="path">File to hash.</param>
    /// <returns>64 lowercase hexadecimal characters.</returns>
    public static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// Maps a package-relative path (forward slashes, already checked by the manifest validator
    /// for <c>..</c> and absolute forms) under <paramref name="root"/>; <see langword="null"/> when
    /// it would leave it anyway.
    /// </summary>
    private static string? Resolve(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        string fullRoot = Path.GetFullPath(root);
        string candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return candidate.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? candidate
            : null;
    }
}
