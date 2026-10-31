using HackerOs.Platform.Core.Apps.Packages;
using Microsoft.Extensions.DependencyInjection;

namespace HackerOs.Platform.Blazor.Shell;

/// <summary>
/// What the desktop and mobile shells do with the Hack Store's update check (ADR 0188 §3,
/// ADR 0215 §1): start it once the desktop shows and stop it with the shell. Updates are shown by
/// a notification only (Benoit, Q3 C): no badge. Does nothing on a host without
/// <see cref="AppUpdateMonitor"/>.
/// </summary>
internal sealed class AppUpdateAttention : IDisposable
{
    private readonly AppUpdateMonitor? _monitor;

    /// <summary>Creates the binding.</summary>
    /// <param name="services">The shell's services; <see cref="AppUpdateMonitor"/> may be missing.</param>
    public AppUpdateAttention(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _monitor = services.GetService<AppUpdateMonitor>();
    }

    /// <summary>Checks now, then every <c>interval-hours</c> (ADR 0188 §3).</summary>
    public void Start() => _monitor?.Start();

    /// <inheritdoc />
    public void Dispose() => _monitor?.Stop();
}
