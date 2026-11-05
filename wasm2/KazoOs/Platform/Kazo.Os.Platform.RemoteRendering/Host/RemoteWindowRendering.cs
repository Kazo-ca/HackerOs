using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Blazor.Windows;
using Kazo.Os.Platform.Core.Discovery;

namespace Kazo.Os.Platform.RemoteRendering.Host;

/// <summary>
/// Puts the trial's apps in a worker (ADR 0133): an app listed in
/// <see cref="RemoteWindowRenderingOptions.Apps"/> whose window has no JavaScript of its own is hosted
/// by <see cref="RemoteWindowHost"/>; every other app renders on the page as before.
/// </summary>
internal sealed class RemoteWindowRendering(RemoteWindowRenderingOptions options) : IRemoteWindowRendering
{
    private readonly HashSet<string> _apps = new(options.Apps, StringComparer.Ordinal);

    /// <inheritdoc />
    public Type? GetHostComponent(AppDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return options.Enabled && descriptor.Manifest.Kind == AppKind.Window && _apps.Contains(descriptor.Manifest.Id)
            ? typeof(RemoteWindowHost)
            : null;
    }
}
