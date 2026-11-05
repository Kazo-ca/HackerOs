using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Kazo.Os.Platform.WorkerHost.Worker;

namespace Kazo.Os.Platform.WorkerHost.Browser;

/// <summary>
/// The .NET entry point of a Kazo OS Web Worker (ADR 0070, section 4), called by
/// <c>workerBootstrap.js</c> (in <c>Kazo.Os.Infrastructure.Browser/wwwroot</c>) once the worker's own
/// runtime is loaded: <see cref="Start"/>, then <see cref="Receive"/> for every message from the OS.
/// </summary>
[SupportedOSPlatform("browser")]
public static partial class BrowserWorkerEntryPoint
{
    private static WorkerProcessHost? s_host;

    /// <summary>
    /// Creates the worker's host. Called once, before any message. On a cross-origin isolated page
    /// the bootstrap offers <c>callSync</c> (ADR 0131, section 4), and the gateways' synchronous
    /// members wait for the OS with <c>Atomics.wait</c>.
    /// </summary>
    [JSExport]
    public static void Start() =>
        s_host ??= new WorkerProcessHost(
            Post,
            new BrowserWorkerAssemblyLoader(),
            canMeterCpu: true,
            SyncAvailable() ? static (id, message, timeout) => CallSync(id, message, (int)timeout.TotalMilliseconds) : null);

    /// <summary>Handles one message from the OS.</summary>
    /// <param name="message">The JSON text.</param>
    [JSExport]
    public static void Receive(string message) => s_host?.Receive(message);

    /// <summary><c>self.postMessage</c>, given by the bootstrap.</summary>
    [JSImport("post", "kazoos-worker")]
    private static partial void Post(string message);

    /// <summary>Whether the bootstrap has a shared call slot (the page is cross-origin isolated).</summary>
    [JSImport("syncAvailable", "kazoos-worker")]
    private static partial bool SyncAvailable();

    /// <summary>Posts call <paramref name="id"/> and blocks in <c>Atomics.wait</c> for its answer; <see langword="null"/> after the timeout.</summary>
    [JSImport("callSync", "kazoos-worker")]
    private static partial string? CallSync(int id, string message, int timeoutMs);
}

/// <summary>
/// Loads an app's assembly in a worker: the lazy-loaded ones (the commands) are fetched first through
/// the runtime's <c>loadLazyAssembly</c>, as Blazor's <c>LazyAssemblyLoader</c> does on the page.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed partial class BrowserWorkerAssemblyLoader : ResolvedAssemblyLoader
{
    private readonly HashSet<string> _loaded = new(StringComparer.Ordinal);

    public override async Task PreloadAsync(IReadOnlyList<string> assemblyFiles)
    {
        foreach (string file in assemblyFiles)
        {
            await LoadLazyAsync(file).ConfigureAwait(true);
        }
    }

    public override async Task<Type> LoadEntryTypeAsync(string assemblyFile, string typeName)
    {
        await LoadLazyAsync(assemblyFile).ConfigureAwait(true);
        return ResolveType(assemblyFile, typeName);
    }

    private bool _satellitesLoaded;

    private async Task LoadLazyAsync(string assemblyFile)
    {
        if (!_satellitesLoaded)
        {
            // ADR 0104: a command prints in its own process's language; the French satellites are
            // loaded once, before the first app assembly, as the page does at start.
            _satellitesLoaded = true;
            try
            {
                await LoadSatelliteAssemblies(["fr"]).ConfigureAwait(true);
            }
            catch (JSException)
            {
                // Texts stay in English.
            }
        }

        string name = Path.GetFileNameWithoutExtension(assemblyFile);
        if (!_loaded.Add(name) || AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == name))
        {
            return;
        }

        try
        {
            await LoadLazyAssembly(name + ".wasm").ConfigureAwait(true);
        }
        catch (JSException)
        {
            // Not a lazy-loaded assembly: it is part of the boot set and loads on first use.
        }
    }

    /// <summary>The runtime's own lazy loader (the API behind Blazor's <c>LazyAssemblyLoader</c>).</summary>
    [JSImport("INTERNAL.loadLazyAssembly")]
    private static partial Task<bool> LoadLazyAssembly(string assemblyNameToLoad);

    /// <summary>The runtime's satellite loader (the API behind Blazor's culture loader).</summary>
    [JSImport("INTERNAL.loadSatelliteAssemblies")]
    private static partial Task LoadSatelliteAssemblies(string[] culturesToLoad);
}
