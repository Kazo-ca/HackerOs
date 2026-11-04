#pragma warning disable BL0006 // The test renderer attaches components directly, as ASP.NET Core's own component tests do.
using System.Reflection;
using System.Runtime.ExceptionServices;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Blazor.Display;
using Kazo.Os.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.AppSdk.Blazor.Tests;

/// <summary>ADR 0117 §2: a window app sets its whole window title through <see cref="WindowAppBase"/>.</summary>
public sealed class WindowAppBaseWindowTitleTests
{
    [Fact]
    public async Task A_title_set_during_initialization_reaches_the_window_before_the_first_render()
    {
        RecordingGateway gateway = new("Title App");
        using TitleRenderer renderer = new();
        TitleHost host = new() { Gateway = gateway, Context = new MinimalAppExecutionContext(Manifest()) };

        await renderer.RenderAsync(host);

        Assert.Equal(["photo.png — Title App"], gateway.Requests);
        Assert.Equal("photo.png — Title App", host.App!.TitleAtFirstRender);
    }

    [Fact]
    public async Task The_same_title_twice_is_sent_once()
    {
        RecordingGateway gateway = new("Title App");
        using TitleRenderer renderer = new();
        TitleHost host = new() { Gateway = gateway, Context = new MinimalAppExecutionContext(Manifest()) };
        await renderer.RenderAsync(host);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await host.App!.SetAsync("photo.png — Title App");
            await host.App.SetAsync("*photo.png — Title App");
            await host.App.SetAsync("*photo.png — Title App");
        });

        Assert.Equal(["photo.png — Title App", "*photo.png — Title App"], gateway.Requests);
        Assert.Equal("*photo.png — Title App", host.App!.Title);
    }

    [Fact]
    public async Task Without_a_window_manager_the_title_stays_readable_and_null_restores_the_name()
    {
        TitleApp app = new() { InitialTitle = null };
        app.Bind(new MinimalAppExecutionContext(Manifest()));

        Assert.Equal("Title App", app.Title);
        await app.SetAsync("  notes.txt — Title App  ");
        Assert.Equal("notes.txt — Title App", app.Title);
        await app.SetAsync(null);
        Assert.Equal("Title App", app.Title);
    }

    [Fact]
    public async Task The_convention_uses_the_localized_name_and_marks_unsaved_documents()
    {
        RecordingGateway gateway = new("Application de titre");
        using TitleRenderer renderer = new();
        TitleHost host = new() { Gateway = gateway, Context = new MinimalAppExecutionContext(Manifest()) };
        await renderer.RenderAsync(host);

        Assert.Equal("Application de titre", host.App!.Format(null));
        Assert.Equal("Application de titre", host.App.Format("   "));
        Assert.Equal("Documents — Application de titre", host.App.Format("Documents"));
        Assert.Equal("*photo.png — Application de titre", host.App.Format("photo.png", modified: true));
    }

    [Fact]
    public async Task Followed_detached_surfaces_take_the_app_title()
    {
        TitleApp app = new() { InitialTitle = null };
        app.Bind(new MinimalAppExecutionContext(Manifest()));
        RecordingSurface before = new();
        using IDisposable trackedBefore = app.Track(before);

        await app.SetAsync("slides.md — Title App");
        RecordingSurface after = new();
        using IDisposable trackedAfter = app.Track(after);

        Assert.Equal(["slides.md — Title App"], before.Titles);
        Assert.Equal(["slides.md — Title App"], after.Titles);
    }

    [Fact]
    public async Task A_cancelled_request_changes_nothing()
    {
        TitleApp app = new() { InitialTitle = null };
        app.Bind(new MinimalAppExecutionContext(Manifest()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => app.SetAsync("ignored", new CancellationToken(canceled: true)).AsTask());

        Assert.Equal("Title App", app.Title);
    }

    private static AppManifest Manifest() => new()
    {
        Id = "org.kazoos.test-title",
        Name = "Title App",
        Version = "1.0.0",
        PublisherId = "org.kazoos",
        Description = "Window title test app.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.AppSdk.Blazor.Tests", "TitleApp"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None
    };

    private sealed class TitleApp : WindowAppBase
    {
        public string? InitialTitle { get; set; } = "photo.png — Title App";

        public string? TitleAtFirstRender { get; private set; }

        public string Title => WindowTitle;

        public void Bind(IAppExecutionContext context) => AppContext = context;

        public ValueTask SetAsync(string? title, CancellationToken cancellationToken = default) =>
            SetWindowTitleAsync(title, cancellationToken);

        public string Format(string? document, bool modified = false) => FormatWindowTitle(document, modified);

        public IDisposable Track(IDisplaySurface surface) => TrackDisplaySurface(surface);

        protected override void OnAppInitialized()
        {
            if (InitialTitle is not null)
            {
                // Synchronous completion: the gateway applies the title at once.
                _ = SetWindowTitleAsync(InitialTitle);
            }
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder) => TitleAtFirstRender ??= WindowTitle;
    }

    /// <summary>Plays the platform's part: cascades the window gateway around the app.</summary>
    private sealed class TitleHost : ComponentBase
    {
        public IAppWindowGateway Gateway { get; init; } = null!;

        public IAppExecutionContext Context { get; init; } = null!;

        public TitleApp? App { get; private set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<IAppWindowGateway>>(0);
            builder.AddComponentParameter(1, "Value", Gateway);
            builder.AddComponentParameter(2, "IsFixed", true);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(child =>
            {
                child.OpenComponent<TitleApp>(0);
                child.AddComponentParameter(1, nameof(WindowAppBase.AppContext), Context);
                child.AddComponentReferenceCapture(2, component => App = (TitleApp)component);
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    private sealed class RecordingGateway(string defaultTitle) : IAppWindowGateway
    {
        public List<string?> Requests { get; } = [];

        public string Title { get; private set; } = defaultTitle;

        public string DefaultTitle { get; } = defaultTitle;

        public ValueTask<string> SetTitleAsync(string? title, CancellationToken cancellationToken = default)
        {
            Requests.Add(title);
            Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle : title.Trim();
            return ValueTask.FromResult(Title);
        }
    }

    private sealed class RecordingSurface : IDisplaySurface
    {
        public List<string> Titles { get; } = [];

        public string Id { get; } = $"surface-{Guid.NewGuid():N}";

        public string Title { get; private set; } = "Detached";

        public int Width => 800;

        public int Height => 600;

        public bool HasFocus => false;

        public DisplaySurfaceState State => DisplaySurfaceState.Open;

        public event Action<IDisplaySurface>? Changed
        {
            add { }
            remove { }
        }

        public ValueTask SetTitleAsync(string title)
        {
            Titles.Add(title);
            Title = title;
            return ValueTask.CompletedTask;
        }

        public ValueTask FocusAsync() => ValueTask.CompletedTask;

        public ValueTask CloseAsync() => ValueTask.CompletedTask;

        public ValueTask MountAsync(ElementReference content) => ValueTask.CompletedTask;

        public ValueTask UnmountAsync(ElementReference content) => ValueTask.CompletedTask;
    }

    /// <summary>Attaches components to a real render handle, discarding render batches (no bUnit in this solution).</summary>
    private sealed class TitleRenderer() : Renderer(new NoServices(), NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public Task RenderAsync(IComponent component) =>
            Dispatcher.InvokeAsync(async () =>
            {
                int id = AssignRootComponentId(component);
                await RenderRootComponentAsync(id, ParameterView.Empty);
            });

        protected override void HandleException(Exception exception) => ExceptionDispatchInfo.Capture(exception).Throw();

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }

    /// <summary>Resolves the window base's injected services with inert stand-ins.</summary>
    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType switch
        {
            _ when serviceType == typeof(IWindowAppFrameworkLifecycle) => NullWindowAppFrameworkLifecycle.Instance,
            { IsInterface: true, Namespace: "Kazo.Os.AppSdk.Blazor" or "Kazo.Os.App.Abstractions.Policy" } => typeof(DispatchProxy)
                .GetMethod(nameof(DispatchProxy.Create), 2, Type.EmptyTypes)!
                .MakeGenericMethod(serviceType, typeof(UnusedServiceProxy))
                .Invoke(null, null),
            _ => null
        };
    }

    /// <summary>A service the title tests never call.</summary>
    public class UnusedServiceProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException($"{targetMethod?.Name} is not used by this test.");
    }
}
