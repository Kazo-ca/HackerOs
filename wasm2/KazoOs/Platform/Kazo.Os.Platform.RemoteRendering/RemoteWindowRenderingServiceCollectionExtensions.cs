using Kazo.Os.Platform.Blazor.Windows;
using Kazo.Os.Platform.RemoteRendering.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Kazo.Os.Platform.RemoteRendering;

/// <summary>Registers the trial of window apps rendered in a worker (ADR 0129, ADR 0133).</summary>
public static class RemoteWindowRenderingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the trial: when <see cref="RemoteWindowRenderingOptions.Enabled"/> is
    /// <see langword="true"/>, the listed apps' windows render in a worker. Needs the Web Worker pool
    /// (<c>AddHackerOsWorkerProcesses</c>); with the trial off nothing changes.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="options">The trial's settings.</param>
    /// <returns>The same services.</returns>
    public static IServiceCollection AddHackerOsRemoteWindowRendering(this IServiceCollection services, RemoteWindowRenderingOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        if (options.Enabled)
        {
            services.AddSingleton<IRemoteWindowRendering, RemoteWindowRendering>();
        }

        return services;
    }
}
