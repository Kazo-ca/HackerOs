using Kazo.Os.Platform.Blazor.LazyLoading;
using Microsoft.JSInterop;

namespace Kazo.Os.Ecosystem;

/// <summary>
/// Reads the running build from the page's own import map and the served build from a fresh,
/// uncached request for the page (ADR 0162), through the host's <c>hostBuild.js</c> module.
/// </summary>
internal sealed class BrowserHostBuildProbe(IJSRuntime runtime) : IHostBuildProbe
{
    private const string ModulePath = "./_content/Kazo.Os.Ecosystem.Host/hostBuild.js";

    /// <inheritdoc />
    public async ValueTask<string?> GetRunningBuildAsync(CancellationToken cancellationToken) =>
        HostBuildIdentity.FromMarkup(await InvokeAsync("runningBuildMarkup", cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public async ValueTask<string?> GetServedBuildAsync(CancellationToken cancellationToken) =>
        HostBuildIdentity.FromMarkup(await InvokeAsync("servedBuildMarkup", cancellationToken).ConfigureAwait(false));

    private async Task<string?> InvokeAsync(string function, CancellationToken cancellationToken)
    {
        await using IJSObjectReference module = await runtime
            .InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath)
            .ConfigureAwait(false);
        return await module.InvokeAsync<string?>(function, cancellationToken).ConfigureAwait(false);
    }
}
