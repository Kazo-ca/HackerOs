using HackerOs.AppSdk;
using HackerOs.AppSdk.Blazor;
using HackerOs.AppSdk.Blazor.ContextMenus;
using HackerOs.Platform.Blazor.ContextMenus;
using System.Diagnostics.CodeAnalysis;
using HackerOs.Platform.Core.Discovery;
using HackerOs.Platform.Core.Shell;
using HackerOs.Windowing.Core;
using HackerOs.Windowing.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace HackerOs.Platform.Blazor.Windows;

/// <summary>Renders one validated Window app component with its bound execution context.</summary>
public sealed class WindowAppRenderer : ComponentBase, IDisposable
{
    private IDisposable? _closeGuardRegistration;
    private IDisposable? _backHandlerRegistration;
    private IDisposable? _windowEventsRegistration;

    /// <summary>Gets or sets the authoritative window snapshot.</summary>
    [Parameter, EditorRequired]
    public WindowRuntimeState Window { get; set; } = null!;

    /// <summary>Gets or sets the trusted catalog descriptor.</summary>
    [Parameter, EditorRequired]
    public AppDescriptor Descriptor { get; set; } = null!;

    /// <summary>Gets or sets the execution context bound to this app instance.</summary>
    [Parameter, EditorRequired]
    public IAppExecutionContext AppContext { get; set; } = null!;

    /// <summary>Gets or sets the platform registry for the rendered app's close guard.</summary>
    [Parameter, EditorRequired]
    public WindowCloseGuardRegistry CloseGuards { get; set; } = null!;

    /// <summary>Gets or sets the platform registry for the rendered app's Back handler.</summary>
    [Parameter, EditorRequired]
    public AppBackHandlerRegistry BackHandlers { get; set; } = null!;

    /// <summary>
    /// Gets or sets the dispatcher of window events to the app's <c>OnWindow*</c> hooks (ADR 0095 §1).
    /// Optional: without it the app receives no window events.
    /// </summary>
    [Parameter]
    public WindowAppEventDispatcher? WindowEvents { get; set; }

    /// <summary>
    /// Gets or sets the factory of the app's window gateway (ADR 0117 §2), through which
    /// <see cref="WindowAppBase.SetWindowTitleAsync"/> renames this window. Optional: without it the
    /// app's title stays inside the app.
    /// </summary>
    [Parameter]
    public AppWindowGatewayFactory? WindowGateways { get; set; }

    /// <summary>Gets or sets the services, to find the optional <see cref="IRemoteWindowRendering"/> (ADR 0133).</summary>
    [Inject]
    private IServiceProvider Services { get; set; } = null!;

    private IAppWindowGateway? _windowGateway;
    private WindowId _windowGatewayId;
    private ContextMenuScope? _menus;

    /// <inheritdoc />
    /// <remarks>
    /// The gateway is created here, before the app component is instantiated, so it reaches the app
    /// with its first parameters and a title set in its initialization is shown with the window.
    /// </remarks>
    protected override void OnParametersSet()
    {
        WindowAppRenderValidator.Validate(Window, Descriptor, AppContext);
        if (WindowGateways is null)
        {
            _windowGateway = null;
        }
        else if (_windowGateway is null || _windowGatewayId != Window.Id)
        {
            _windowGateway = WindowGateways.Create(Window.Id, AppContext.Manifest);
            _windowGatewayId = Window.Id;
        }

        // ADR 0150 §4: the window's own context menus, forgotten when the window closes. Optional:
        // a host without the menu service (a unit test) renders the app without them.
        if (_menus is null && Services.GetService(typeof(ContextMenuService)) is ContextMenuService menus)
        {
            _menus = menus.CreateScope(AppContext);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The app is wrapped in a fixed <see cref="CascadingValue{TValue}"/> carrying its context, so
    /// <see cref="HackComponentBase"/> children run inside the same ambient app scope as the window
    /// (ADR 0043). The context never changes for a rendered window (rebinding is rejected).
    /// </remarks>
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<CascadingValue<IAppExecutionContext>>(0);
        builder.AddComponentParameter(1, nameof(CascadingValue<IAppExecutionContext>.Value), AppContext);
        builder.AddComponentParameter(2, nameof(CascadingValue<IAppExecutionContext>.IsFixed), true);
        builder.AddComponentParameter(3, nameof(CascadingValue<IAppExecutionContext>.ChildContent), (RenderFragment)RenderApp);
        builder.CloseComponent();
    }

    private void RenderApp(RenderTreeBuilder builder)
    {
        if (_menus is null)
        {
            RenderWithGateway(builder);
            return;
        }

        // ADR 0150 §4: Menus.Attach and HackContextMenu find the window's menus in cascade.
        builder.OpenComponent<CascadingValue<IHackMenuHost>>(0);
        builder.AddComponentParameter(1, nameof(CascadingValue<IHackMenuHost>.Value), _menus);
        builder.AddComponentParameter(2, nameof(CascadingValue<IHackMenuHost>.IsFixed), true);
        builder.AddComponentParameter(3, nameof(CascadingValue<IHackMenuHost>.ChildContent), (RenderFragment)RenderWithGateway);
        builder.CloseComponent();
    }

    private void RenderWithGateway(RenderTreeBuilder builder)
    {
        if (_windowGateway is null)
        {
            RenderEntryPoint(builder);
            return;
        }

        // ADR 0117 §2: the window's own gateway, fixed for the window's lifetime like the context.
        builder.OpenComponent<CascadingValue<IAppWindowGateway>>(0);
        builder.AddComponentParameter(1, nameof(CascadingValue<IAppWindowGateway>.Value), _windowGateway);
        builder.AddComponentParameter(2, nameof(CascadingValue<IAppWindowGateway>.IsFixed), true);
        builder.AddComponentParameter(3, nameof(CascadingValue<IAppWindowGateway>.ChildContent), (RenderFragment)RenderEntryPoint);
        builder.CloseComponent();
    }

    private void RenderEntryPoint(RenderTreeBuilder builder)
    {
        // ADR 0129 trial: when the host turned it on for this app, a page-side host shows the content
        // the app renders in its worker; the app component is not created on the page.
        if (Services.GetService(typeof(IRemoteWindowRendering)) is IRemoteWindowRendering remote
            && remote.GetHostComponent(Descriptor) is { } host)
        {
            builder.OpenComponent(3, host);
            builder.AddComponentParameter(4, nameof(Window), Window);
            builder.AddComponentParameter(5, nameof(Descriptor), Descriptor);
            builder.AddComponentParameter(6, nameof(AppContext), AppContext);
            builder.CloseComponent();
            return;
        }

        builder.OpenComponent(0, EntryPointType);
        builder.AddComponentParameter(1, nameof(WindowAppBase.AppContext), AppContext);
        builder.AddComponentReferenceCapture(2, CaptureComponent);
        builder.CloseComponent();
    }

    /// <summary>
    /// The component of the window: the entry point the app declares for the platform active when the window
    /// first rendered (ADR 0171 §1). Kept for the window's life: a platform switch restarts the windows whose
    /// entry point differs (<see cref="Shell.PlatformShellSwitchCoordinator"/>) rather than swapping it live.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    private Type EntryPointType => Descriptor.EntryPointTypeFor(_platform ??= ActivePlatform());

    private HackerOs.App.Abstractions.AppPlatformId? _platform;

    private HackerOs.App.Abstractions.AppPlatformId ActivePlatform() =>
        Services.GetService(typeof(UiPlatformPreferenceService)) is UiPlatformPreferenceService preference
            ? preference.Current.ActivePlatform
            : HackerOs.App.Abstractions.WellKnownAppPlatforms.Desktop;

    private void CaptureComponent(object component)
    {
        _closeGuardRegistration?.Dispose();
        _closeGuardRegistration = component is IWindowCloseGuard guard
            ? CloseGuards.Register(Window.Id, guard)
            : null;

        _backHandlerRegistration?.Dispose();
        _backHandlerRegistration = component is IAppBackHandler backHandler
            ? BackHandlers.Register(Window.Id, backHandler)
            : null;

        _windowEventsRegistration?.Dispose();
        _windowEventsRegistration = component is IWindowAppEventTarget target && WindowEvents is not null
            ? WindowEvents.Register(Window.Id, target)
            : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _closeGuardRegistration?.Dispose();
        _closeGuardRegistration = null;

        _backHandlerRegistration?.Dispose();
        _backHandlerRegistration = null;

        _windowEventsRegistration?.Dispose();
        _windowEventsRegistration = null;

        _menus?.Dispose();
        _menus = null;
    }
}
