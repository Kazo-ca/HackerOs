using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.WebData;

/// <summary>
/// The browser side of the web sites' databases (ADR 0088): owns <c>webSiteDatabases.js</c>, which opens
/// <c>hackweb.*</c> IndexedDB databases only. Schemas, transactions and results cross as JSON text;
/// errors arrive as <see cref="JSException"/> whose message starts with the IndexedDB error name.
/// </summary>
public sealed class WebSiteDatabaseInterop : IAsyncDisposable
{
    internal const string ModulePath = "./_content/Kazo.Os.Infrastructure.Browser/webSiteDatabases.js";

    private readonly IJSRuntime _runtime;
    private readonly SemaphoreSlim _moduleGate = new(1, 1);
    private IJSObjectReference? _module;

    /// <summary>Initializes the interop; the module is imported on first use.</summary>
    /// <param name="runtime">The page's JavaScript runtime.</param>
    public WebSiteDatabaseInterop(IJSRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    /// <summary>Opens <paramref name="databaseName"/> with a schema (JSON of <c>KazoWebDbSchemaDefinition</c>).</summary>
    public async ValueTask OpenAsync(string databaseName, string schemaJson, CancellationToken cancellationToken = default) =>
        await (await ModuleAsync(cancellationToken).ConfigureAwait(false))
            .InvokeVoidAsync("open", cancellationToken, databaseName, schemaJson).ConfigureAwait(false);

    /// <summary>Runs a batch (JSON of <c>KazoWebDbRequest</c>) and returns the JSON array of its results.</summary>
    public async ValueTask<string> ExecuteAsync(string databaseName, string requestJson, CancellationToken cancellationToken = default) =>
        await (await ModuleAsync(cancellationToken).ConfigureAwait(false))
            .InvokeAsync<string>("execute", cancellationToken, databaseName, requestJson).ConfigureAwait(false);

    /// <summary>Lists the site databases as JSON (<c>[{ name, version, stores: [{ name, recordCount }], sizeBytes }]</c>).</summary>
    /// <param name="knownNamesJson">Names to describe when the browser cannot enumerate its databases.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<string> ListAsync(string knownNamesJson, CancellationToken cancellationToken = default) =>
        await (await ModuleAsync(cancellationToken).ConfigureAwait(false))
            .InvokeAsync<string>("list", cancellationToken, knownNamesJson).ConfigureAwait(false);

    /// <summary>Deletes a site database.</summary>
    public async ValueTask DeleteAsync(string databaseName, CancellationToken cancellationToken = default) =>
        await (await ModuleAsync(cancellationToken).ConfigureAwait(false))
            .InvokeVoidAsync("remove", cancellationToken, databaseName).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // The page is going away.
            }
        }

        _moduleGate.Dispose();
    }

    private async ValueTask<IJSObjectReference> ModuleAsync(CancellationToken cancellationToken)
    {
        if (_module is not null)
        {
            return _module;
        }

        await _moduleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _module ??= await _runtime.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath).ConfigureAwait(false);
        }
        finally
        {
            _moduleGate.Release();
        }
    }
}
