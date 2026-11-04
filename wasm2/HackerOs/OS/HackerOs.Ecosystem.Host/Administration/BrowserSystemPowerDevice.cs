using HackerOs.Infrastructure.Browser.Schema;
using HackerOs.Platform.Core.Power;
using Microsoft.JSInterop;

namespace HackerOs.Ecosystem.Administration;

/// <summary>
/// The browser as the machine's "hardware" for <see cref="SystemPowerService"/> (ADR 0056,
/// section 11): a restart reloads the page, and erasing the storage leaves nothing of KazoOS in
/// the browser (ADR 0227): every IndexedDB database, Cache Storage, localStorage, sessionStorage,
/// the origin private file system, cookies and the service worker.
/// </summary>
internal sealed class BrowserSystemPowerDevice(IJSRuntime runtime) : ISystemPowerDevice
{
    private const string IndexedDbModulePath = "./_content/HackerOs.Infrastructure.Browser/indexedDb.js";
    private const string FactoryResetModulePath = "./_content/HackerOs.Ecosystem.Host/hostFactoryReset.js";

    /// <inheritdoc />
    public async Task RestartAsync(CancellationToken cancellationToken = default) =>
        await runtime.InvokeVoidAsync("location.reload", cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task EraseStorageAsync(CancellationToken cancellationToken = default)
    {
        // The OS database first, through the module that holds its cached connections: it closes
        // them, so this deletion is never blocked by the page itself.
        await using (IJSObjectReference indexedDb = await runtime
            .InvokeAsync<IJSObjectReference>("import", cancellationToken, IndexedDbModulePath)
            .ConfigureAwait(false))
        {
            await indexedDb.InvokeVoidAsync("deleteDatabase", cancellationToken, HackerOsIndexedDbSchema.DatabaseName).ConfigureAwait(false);
        }

        // ADR 0227: then everything else the origin keeps. A step that fails is reported by the
        // module and does not stop the others; the reload follows either way.
        await using IJSObjectReference reset = await runtime
            .InvokeAsync<IJSObjectReference>("import", cancellationToken, FactoryResetModulePath)
            .ConfigureAwait(false);
        string[] failed = await reset.InvokeAsync<string[]>("eraseEverything", cancellationToken).ConfigureAwait(false);
        foreach (string step in failed)
        {
            Console.WriteLine($"KazoOS factory reset: could not erase {step}");
        }
    }
}
