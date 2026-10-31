using HackerOs.Platform.Core.Apps.Packages;
using Microsoft.Extensions.DependencyInjection;

namespace HackerOs.Platform.Blazor.Shell;

/// <summary>
/// What the desktop and mobile shells do with the Hack Store's update check (ADR 0188 §3,
/// ADR 0212 §5): start it once the desktop shows, stop it with the shell, and redraw the count on
/// the Store's icon when it moves. Does nothing on a host without <see cref="AppUpdateMonitor"/>.
/// </summary>
internal sealed class AppUpdateAttention : IDisposable
{
    private readonly AppUpdateMonitor? _monitor;
    private readonly Action _changed;

    /// <summary>Creates the binding.</summary>
    /// <param name="services">The shell's services; <see cref="AppUpdateMonitor"/> may be missing.</param>
    /// <param name="changed">Redraws the shell; may be called off the renderer's context.</param>
    public AppUpdateAttention(IServiceProvider services, Action changed)
    {
        ArgumentNullException.ThrowIfNull(services);
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        _monitor = services.GetService<AppUpdateMonitor>();
        if (_monitor is not null)
        {
            _monitor.Changed += _changed;
        }
    }

    /// <summary>Gets the count drawn on an app's icon.</summary>
    /// <param name="appId">The app.</param>
    public int GetBadgeCount(string appId) => _monitor?.GetBadgeCount(appId) ?? 0;

    /// <summary>Checks now, then every <c>interval-hours</c> (ADR 0188 §3).</summary>
    public void Start() => _monitor?.Start();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_monitor is not null)
        {
            _monitor.Changed -= _changed;
            _monitor.Stop();
        }
    }
}
