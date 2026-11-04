using System.Net;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Apps;

namespace Kazo.Os.Platform.Blazor.LazyLoading;

/// <summary>
/// Browser source of app assets (ADR 0098 §5): an asset declared as <c>icons/app.svg</c> by an app
/// whose entry point lives in <c>Contoso.Notes.dll</c> is the static web asset
/// <c>_content/Contoso.Notes/icons/app.svg</c>, published by Blazor from the app project's
/// <c>wwwroot/</c>. <see cref="AppAssetStore"/> checks the bytes against the manifest.
/// </summary>
public sealed class StaticWebAssetAppAssetSource : IAppAssetSource, IDisposable
{
    private readonly HttpClient _http;

    /// <summary>Creates the source for the page's base address.</summary>
    /// <param name="baseAddress">The base URI the host serves <c>_content/</c> from (the WebAssembly host's <c>BaseAddress</c>).</param>
    public StaticWebAssetAppAssetSource(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        _http = new HttpClient { BaseAddress = baseAddress };
    }

    /// <summary>Gets the URL of an asset relative to the host's base address.</summary>
    /// <param name="manifest">The app the asset belongs to.</param>
    /// <param name="assetPath">The declared package-relative path.</param>
    /// <returns>For example <c>_content/Contoso.Notes/icons/app.svg</c>.</returns>
    public static string RelativeUrl(AppManifest manifest, string assetPath)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);
        string assembly = manifest.EntryPoint?.Assembly
            ?? manifest.Platform?.EntryPoints.FirstOrDefault()?.Assembly
            ?? throw new InvalidOperationException($"'{manifest.Id}' has no entry-point assembly.");
        string package = assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? assembly[..^4] : assembly;
        return $"_content/{Uri.EscapeDataString(package)}/{string.Join('/', assetPath.Split('/').Select(Uri.EscapeDataString))}";
    }

    /// <inheritdoc />
    public async ValueTask<byte[]?> ReadAsync(AppManifest manifest, string assetPath, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _http.GetAsync(RelativeUrl(manifest, assetPath), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
