using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;

namespace HackerOs.Infrastructure.Browser.Apps;

/// <summary>
/// The page's cache of the <c>wwwroot/</c> files of installed apps (ADR 0206): the C# half of
/// <c>wwwroot/installedAppAssets.js</c>, which this class imports itself (no host references it,
/// AGENTS.md). The host's service worker answers <c>_content/&lt;assembly&gt;/&lt;path&gt;</c> from
/// that cache, so an installed app finds its files at the address a built-in app uses.
/// </summary>
/// <remarks>Every address is relative to the page's base, for example <c>_content/Kazo.Sample/x.js</c>.</remarks>
[SupportedOSPlatform("browser")]
public static partial class BrowserInstalledAppAssets
{
    private const string ModuleName = "hackeros-installed-app-assets";
    private const string ModulePath = "_content/HackerOs.Infrastructure.Browser/installedAppAssets.js";
    private static Task? _import;

    /// <summary>
    /// Makes sure a service worker controls the page (on a development host, registers the one that
    /// only serves installed apps) and returns whether one does.
    /// </summary>
    /// <param name="timeout">How long to wait for a new worker to take control.</param>
    public static async Task<bool> PrepareAsync(TimeSpan timeout)
    {
        await ImportAsync().ConfigureAwait(true);
        return await Prepare((int)timeout.TotalMilliseconds).ConfigureAwait(true);
    }

    /// <summary>Lists the cached addresses.</summary>
    public static async Task<IReadOnlyList<string>> ListAsync()
    {
        await ImportAsync().ConfigureAwait(true);
        return JsonSerializer.Deserialize(await List().ConfigureAwait(true), BrowserInstalledAppAssetsJsonContext.Default.StringArray) ?? [];
    }

    /// <summary>Stores one file under its address.</summary>
    /// <param name="url">The address, relative to the page's base.</param>
    /// <param name="bytes">The verified bytes.</param>
    /// <param name="contentType">The <c>Content-Type</c> the service worker answers with.</param>
    public static async Task PutAsync(string url, byte[] bytes, string contentType)
    {
        await ImportAsync().ConfigureAwait(true);
        await Put(url, bytes, contentType).ConfigureAwait(true);
    }

    /// <summary>Removes one address; <see langword="true"/> when it was cached.</summary>
    /// <param name="url">The address, relative to the page's base.</param>
    public static async Task<bool> RemoveAsync(string url)
    {
        await ImportAsync().ConfigureAwait(true);
        return await Remove(url).ConfigureAwait(true);
    }

    /// <summary>
    /// Links a cached style sheet to the page (an installed app's scoped CSS, ADR 0221 §2), once per
    /// address; <see langword="true"/> when the link was added.
    /// </summary>
    /// <param name="url">The address, relative to the page's base.</param>
    public static async Task<bool> LinkStyleSheetAsync(string url)
    {
        await ImportAsync().ConfigureAwait(true);
        return LinkStyleSheet(url);
    }

    private static Task ImportAsync() => _import ??= ImportCoreAsync();

    private static async Task ImportCoreAsync()
    {
        string baseUri = JSHost.GlobalThis.GetPropertyAsJSObject("document")?.GetPropertyAsString("baseURI") ?? "/";
        await JSHost.ImportAsync(ModuleName, new Uri(new Uri(baseUri), ModulePath).ToString()).ConfigureAwait(true);
    }

    [JSImport("prepare", ModuleName)]
    private static partial Task<bool> Prepare(int timeoutMs);

    [JSImport("list", ModuleName)]
    private static partial Task<string> List();

    [JSImport("put", ModuleName)]
    private static partial Task Put(string url, byte[] bytes, string contentType);

    [JSImport("remove", ModuleName)]
    private static partial Task<bool> Remove(string url);

    [JSImport("linkStyleSheet", ModuleName)]
    private static partial bool LinkStyleSheet(string url);
}

/// <summary>Trim-safe JSON of <see cref="BrowserInstalledAppAssets.ListAsync"/>.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class BrowserInstalledAppAssetsJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
