#pragma warning disable BL0006 // The test renderer attaches components directly, as ASP.NET Core's own component tests do.
using System.Reflection;
using System.Runtime.ExceptionServices;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.AppSdk.Blazor.Tests;

/// <summary>
/// ADR 0043: window apps and <see cref="KazoComponentBase"/> children run their lifecycle, UI events and
/// after-render work inside the app's <see cref="KazoExecutionContext"/> scope.
/// </summary>
public sealed class AmbientContextComponentTests
{
    [Fact]
    public async Task Window_app_lifecycle_events_and_after_render_see_the_app_context()
    {
        MinimalAppExecutionContext context = new(WindowManifest());
        using AmbientRenderer renderer = new();
        ProbeWindowApp app = new();

        await renderer.RenderAsync(app, ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(WindowAppBase.AppContext)] = context }));
        await renderer.Dispatcher.InvokeAsync(() =>
            ((IHandleEvent)app).HandleEventAsync(new EventCallbackWorkItem(app.OnClickAsync), null));

        Assert.Same(context, app.SeenOnInitialized);
        Assert.Same(context, app.SeenAfterRender);
        Assert.Same(context, app.SeenOnClick);
        Assert.Same(context, app.SeenAfterClickAwait);
        Assert.Null(KazoExecutionContext.CurrentOrNull);
    }

    [Fact]
    public async Task Child_components_receive_the_context_by_cascade()
    {
        MinimalAppExecutionContext context = new(WindowManifest());
        using AmbientRenderer renderer = new();
        CascadeHost host = new();

        await renderer.RenderAsync(host, ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(CascadeHost.Context)] = context }));
        ProbeChild child = host.Child!;
        await renderer.Dispatcher.InvokeAsync(() =>
            ((IHandleEvent)child).HandleEventAsync(new EventCallbackWorkItem(child.OnClick), null));

        Assert.Same(context, child.SeenOnInitialized);
        Assert.Same(context, child.SeenAfterRender);
        Assert.Same(context, child.SeenOnClick);
    }

    private static AppManifest WindowManifest() => new()
    {
        Id = "org.hackeros.test-ambient",
        Name = "Ambient Window",
        Version = "1.0.0",
        PublisherId = "org.hackeros",
        Description = "Ambient context component test.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.AppSdk.Blazor.Tests", "ProbeWindowApp"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None
    };

    private sealed class ProbeWindowApp : WindowAppBase
    {
        public IAppExecutionContext? SeenOnInitialized { get; private set; }
        public IAppExecutionContext? SeenAfterRender { get; private set; }
        public IAppExecutionContext? SeenOnClick { get; private set; }
        public IAppExecutionContext? SeenAfterClickAwait { get; private set; }

        public async Task OnClickAsync()
        {
            SeenOnClick = KazoExecutionContext.CurrentOrNull;
            await Task.Yield();
            SeenAfterClickAwait = KazoExecutionContext.CurrentOrNull;
        }

        protected override void OnAppInitialized() => SeenOnInitialized = KazoExecutionContext.CurrentOrNull;

        protected override void OnAppAfterRender(bool firstRender) => SeenAfterRender = KazoExecutionContext.CurrentOrNull;
    }

    private sealed class CascadeHost : ComponentBase
    {
        [Parameter]
        public IAppExecutionContext Context { get; set; } = null!;

        public ProbeChild? Child { get; private set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<IAppExecutionContext>>(0);
            builder.AddComponentParameter(1, "Value", Context);
            builder.AddComponentParameter(2, "IsFixed", true);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(child =>
            {
                child.OpenComponent<ProbeChild>(0);
                child.AddComponentReferenceCapture(1, component => Child = (ProbeChild)component);
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    private sealed class ProbeChild : KazoComponentBase
    {
        public IAppExecutionContext? SeenOnInitialized { get; private set; }
        public IAppExecutionContext? SeenAfterRender { get; private set; }
        public IAppExecutionContext? SeenOnClick { get; private set; }

        public void OnClick() => SeenOnClick = KazoExecutionContext.CurrentOrNull;

        protected override void OnInitialized() => SeenOnInitialized = KazoExecutionContext.CurrentOrNull;

        protected override void OnAfterRender(bool firstRender) => SeenAfterRender = KazoExecutionContext.CurrentOrNull;
    }

    /// <summary>Attaches components to a real render handle, discarding render batches (no bUnit in this solution).</summary>
    private sealed class AmbientRenderer() : Renderer(new StubServices(), NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public Task RenderAsync(IComponent component, ParameterView parameters) =>
            Dispatcher.InvokeAsync(async () =>
            {
                int id = AssignRootComponentId(component);
                await RenderRootComponentAsync(id, parameters);
            });

        protected override void HandleException(Exception exception) => ExceptionDispatchInfo.Capture(exception).Throw();

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }

    /// <summary>Resolves the window base's injected services with inert stand-ins.</summary>
    private sealed class StubServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType switch
        {
            _ when serviceType == typeof(IWindowAppFrameworkLifecycle) => NullWindowAppFrameworkLifecycle.Instance,
            { IsInterface: true, Namespace: "Kazo.Os.AppSdk.Blazor" } => typeof(DispatchProxy)
                .GetMethod(nameof(DispatchProxy.Create), 2, Type.EmptyTypes)!
                .MakeGenericMethod(serviceType, typeof(UnusedServiceProxy))
                .Invoke(null, null),
            _ => null
        };
    }

    /// <summary>A service the probe components never call.</summary>
    public class UnusedServiceProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException($"{targetMethod?.Name} is not used by this test.");
    }
}
