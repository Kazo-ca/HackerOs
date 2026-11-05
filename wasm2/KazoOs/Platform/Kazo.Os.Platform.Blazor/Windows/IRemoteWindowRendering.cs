using Kazo.Os.Platform.Core.Discovery;

namespace Kazo.Os.Platform.Blazor.Windows;

/// <summary>
/// Chooses whether a window app's content is rendered in a worker instead of on the page (trial of
/// ADR 0129, verdict in ADR 0133). A host registers one only when it turns the trial on; without it,
/// <see cref="WindowAppRenderer"/> renders every app on the page, as before.
/// </summary>
public interface IRemoteWindowRendering
{
    /// <summary>
    /// Gets the component that hosts <paramref name="descriptor"/>'s window content on the page while
    /// the app runs in a worker, or <see langword="null"/> to render the app here. The component takes
    /// the <see cref="WindowAppRenderer"/>'s <c>Window</c>, <c>Descriptor</c> and <c>AppContext</c>
    /// parameters.
    /// </summary>
    /// <param name="descriptor">The trusted catalog descriptor of the app.</param>
    Type? GetHostComponent(AppDescriptor descriptor);
}
