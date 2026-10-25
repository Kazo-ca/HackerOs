using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace HackerOs.Infrastructure.Browser.Globalization;

/// <summary>
/// Loads the satellite assemblies of HackerOS's languages into the browser's .NET runtime
/// (ADR 0104, section 3; doc/en/wasm/framework/localization.md).
/// </summary>
/// <remarks>
/// <para>
/// Blazor loads only the satellites of the tab's own culture, at start. HackerOS needs the French
/// ones whatever the browser's language, because every process has its own <c>LANG</c>, and those of
/// the lazily loaded apps too, before their main assembly is even fetched. The runtime's
/// <c>loadSatelliteAssemblies</c> (the API behind Blazor's own culture loader) fetches every
/// satellite of the build for the given cultures and loads it into the default load context, where
/// <c>ResourceManager</c> finds it when the app asks.
/// </para>
/// <para>
/// A boot-time <c>loadAllSatelliteResources</c> was tried first (2026-10-17): it downloads the
/// files but registers them in a way the satellite lookup of a lazily loaded assembly does not
/// find, so the runtime path is used instead.
/// </para>
/// </remarks>
public static partial class BrowserSatelliteAssemblies
{
    private static readonly HashSet<string> s_loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim s_gate = new(1, 1);

    /// <summary>
    /// Loads the satellites of <paramref name="cultures"/> once per runtime. Does nothing outside a
    /// browser (the server host reads satellites from disk) or for English, the neutral resources.
    /// </summary>
    /// <param name="cultures">Culture names, e.g. <c>fr</c>.</param>
    /// <returns><see langword="true"/> when every culture is loaded (or needs nothing).</returns>
    public static async Task<bool> LoadAsync(IEnumerable<string> cultures)
    {
        ArgumentNullException.ThrowIfNull(cultures);
        if (!OperatingSystem.IsBrowser())
        {
            return true;
        }

        await s_gate.WaitAsync().ConfigureAwait(true);
        try
        {
            string[] missing = [.. cultures
                .Where(static culture => !string.IsNullOrWhiteSpace(culture) && !culture.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                .Where(static culture => !s_loaded.Contains(culture))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            if (missing.Length == 0)
            {
                return true;
            }

            try
            {
                await LoadSatelliteAssemblies(missing).ConfigureAwait(true);
            }
            catch (JSException)
            {
                // A runtime without the API (or a failed download): texts stay in English.
                return false;
            }

            s_loaded.UnionWith(missing);
            return true;
        }
        finally
        {
            s_gate.Release();
        }
    }

    /// <summary>The runtime's satellite loader (what Blazor's <c>WebAssemblyCultureProvider</c> calls).</summary>
    [SupportedOSPlatform("browser")]
    [JSImport("INTERNAL.loadSatelliteAssemblies")]
    private static partial Task LoadSatelliteAssemblies(string[] culturesToLoad);
}
