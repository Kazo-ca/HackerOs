using Kazo.Os.App.Abstractions;

namespace Kazo.Os.AppSdk.Apps;

/// <summary>
/// Where the browser finds an app's static files (ADR 0164, ADR 0232 §3): a Razor class library
/// serves its <c>wwwroot/</c> under <c>_content/&lt;assembly&gt;/</c>, both for a system app and for an
/// installed one (ADR 0206, ADR 0221). The shell and the SDK share this single formula.
/// </summary>
public static class KazoAppAssets
{
    /// <summary>Builds the page-relative URL of a declared package-relative asset.</summary>
    /// <param name="manifest">The app the asset belongs to.</param>
    /// <param name="assetPath">The declared package-relative path, such as <c>icons/app.svg</c>.</param>
    /// <returns>For example <c>_content/Contoso.Notes/icons/app.svg</c>, every segment escaped.</returns>
    /// <exception cref="InvalidOperationException">The app declares no entry-point assembly.</exception>
    public static string RelativeUrl(AppManifest manifest, string assetPath)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);
        string package = PackageOf(manifest)
            ?? throw new InvalidOperationException($"'{manifest.Id}' has no entry-point assembly.");
        return Build(package, assetPath);
    }

    /// <summary>
    /// The URL of the app's first declared icon, or <see langword="null"/> when it declares none or has no
    /// assembly to serve it from (a command or a web app without a library).
    /// </summary>
    /// <param name="manifest">The app.</param>
    internal static string? IconUrlOf(AppManifest manifest)
    {
        string? icon = manifest.Presentation.IconAssetPaths.Count > 0 ? manifest.Presentation.IconAssetPaths[0] : null;
        string? package = PackageOf(manifest);
        return string.IsNullOrWhiteSpace(icon) || package is null ? null : Build(package, icon);
    }

    /// <summary>The static web assets package name: the entry-point assembly without <c>.dll</c>.</summary>
    private static string? PackageOf(AppManifest manifest)
    {
        string? assembly = manifest.EntryPoint?.Assembly ?? manifest.Platform?.EntryPoints.FirstOrDefault()?.Assembly;
        if (string.IsNullOrWhiteSpace(assembly))
        {
            return null;
        }

        return assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? assembly[..^4] : assembly;
    }

    private static string Build(string package, string assetPath) =>
        $"_content/{Uri.EscapeDataString(package)}/{string.Join('/', assetPath.Split('/').Select(Uri.EscapeDataString))}";
}
