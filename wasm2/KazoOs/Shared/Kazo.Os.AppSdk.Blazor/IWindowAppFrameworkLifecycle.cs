namespace Kazo.Os.AppSdk.Blazor;

/// <summary>
/// Runs Platform-owned post-render setup without exposing browser interop to Window apps.
/// </summary>
/// <remarks>
/// The interactive host replaces the default no-op implementation. App components do not call
/// this service; the sealed <see cref="WindowAppBase"/> lifecycle invokes it before app hooks.
/// </remarks>
public interface IWindowAppFrameworkLifecycle
{
    /// <summary>Runs mandatory framework setup after a Window app render.</summary>
    Task OnAfterRenderAsync(WindowAppBase app, bool firstRender);
}

/// <summary>
/// The default <see cref="IWindowAppFrameworkLifecycle"/>: does nothing, for hosts without browser
/// interop (tests, prerendering) until the interactive host registers its own implementation.
/// </summary>
public sealed class NullWindowAppFrameworkLifecycle : IWindowAppFrameworkLifecycle
{
    /// <summary>The shared stateless instance.</summary>
    public static NullWindowAppFrameworkLifecycle Instance { get; } = new();

    /// <inheritdoc />
    public Task OnAfterRenderAsync(WindowAppBase app, bool firstRender)
    {
        _ = app;
        _ = firstRender;
        return Task.CompletedTask;
    }
}