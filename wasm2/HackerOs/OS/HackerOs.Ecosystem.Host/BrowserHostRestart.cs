using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace HackerOs.Ecosystem;

/// <summary>
/// « Redémarrer » of the Start menu's Power button (ADR 0195): once the session has ended like a
/// sign-out, empties the caches holding the running copy of the OS (the host's
/// <c>hostRestart.js</c> module), then restarts the machine like after a system update.
/// </summary>
internal static class BrowserHostRestart
{
    private const string ModulePath = "./_content/HackerOs.Ecosystem.Host/hostRestart.js";

    /// <summary>Runs the restart; returns only when no power device could reload the page.</summary>
    /// <param name="services">The host's services: the JS runtime and the power service or device.</param>
    public static async Task RestartAsync(IServiceProvider services)
    {
        Func<Task> clearCaches = () => ClearCachesAsync(services.GetService<IJSRuntime>());
        if (services.GetService<HackerOs.Platform.Core.Power.SystemPowerService>() is { } power)
        {
            // Flushes the logs and ends what is left of the session, then reloads the page.
            await power.RestartAfterAsync(clearCaches);
        }
        else if (services.GetService<HackerOs.Platform.Core.Power.ISystemPowerDevice>() is { } device)
        {
            await clearCaches();
            await device.RestartAsync();
        }
    }

    private static async Task ClearCachesAsync(IJSRuntime? runtime)
    {
        if (runtime is null)
        {
            return;
        }

        try
        {
            await using IJSObjectReference module = await runtime.InvokeAsync<IJSObjectReference>("import", ModulePath);
            await module.InvokeAsync<string>("clearCachesForRestart");
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException or TaskCanceledException)
        {
            // A cache that cannot be emptied must not prevent the restart: the page reloads anyway.
        }
    }
}
