using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Kazo.Os.Infrastructure.Browser.Isolation;

/// <summary>
/// Asks the page at start whether it is cross-origin isolated (ADR 0131, sections 1 and 5): the
/// C# half of <c>wwwroot/crossOriginIsolation.js</c>, which this class imports itself (no host
/// references it, AGENTS.md). <c>CrossOriginIsolationProbe.Read</c> (Platform Core) turns the
/// answer into a status.
/// </summary>
/// <remarks>
/// The OS decides on the page's <c>crossOriginIsolated</c>, never on the setting alone: a header
/// lost on the way gives the exact fallback (messages between workers and the OS, asynchronous
/// calls only, estimated worker memory, 100 µs timers).
/// </remarks>
[SupportedOSPlatform("browser")]
public static partial class BrowserCrossOriginIsolation
{
    private const string ModuleName = "hackeros-cross-origin-isolation";
    private const string ModulePath = "_content/Kazo.Os.Infrastructure.Browser/crossOriginIsolation.js";

    /// <summary>
    /// Probes the page and returns the JSON text of what it found, or <see langword="null"/> when the
    /// module could not run. When the browser ignored COEP <c>credentialless</c>, the page reloads
    /// once with <c>require-corp</c> right after this returns.
    /// </summary>
    /// <param name="wanted">Whether the host's setting asks for isolation.</param>
    public static async Task<string?> ProbeAsync(bool wanted)
    {
        try
        {
            string baseUri = JSHost.GlobalThis.GetPropertyAsJSObject("document")?.GetPropertyAsString("baseURI") ?? "/";
            await JSHost.ImportAsync(ModuleName, new Uri(new Uri(baseUri), ModulePath).ToString()).ConfigureAwait(true);
            return await Probe(wanted).ConfigureAwait(true);
        }
        catch (JSException)
        {
            return null;
        }
    }

    [JSImport("probe", ModuleName)]
    private static partial Task<string> Probe(bool wanted);
}
