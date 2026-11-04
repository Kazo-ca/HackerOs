#pragma warning disable BL0006 // The test renderer attaches components directly, as ASP.NET Core's own component tests do.
using System.Runtime.ExceptionServices;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Blazor.Components;
using Kazo.Os.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.AppSdk.Blazor.Tests;

/// <summary>
/// ADR 0148 §1 and §4: a typed host component of the SDK renders the provider the OS resolved for its
/// contract, forwards its ordinary parameters and callbacks, and keeps the host's rights.
/// </summary>
public sealed partial class HackContractComponentTests
{
    private const string Contract = "hackeros.tests-view";

    [Fact]
    public async Task The_provider_receives_the_host_parameters_and_hands_back_its_controller()
    {
        MinimalAppExecutionContext context = new(WindowManifest());
        using ContractRenderer renderer = new(new StubComponentHost(typeof(TestProvider)));
        HostWindow window = new();

        await renderer.RenderAsync(window, context);
        TestView view = window.View!;
        await renderer.WaitUntilAsync(() => view.Controller is not null);

        TestProvider provider = Assert.IsType<TestProvider>(view.Controller);
        Assert.Equal("/home/user", provider.Path);
        Assert.Same(provider, window.ControllerSeen);
        // A parameter this provider does not know yet is captured, not fatal.
        Assert.Equal(true, provider.UnmatchedParameters?["NewerOnly"]);
    }

    [Fact]
    public async Task The_provider_runs_with_the_host_context_and_its_callbacks_run_in_the_host()
    {
        MinimalAppExecutionContext context = new(WindowManifest());
        using ContractRenderer renderer = new(new StubComponentHost(typeof(TestProvider)));
        HostWindow window = new();

        await renderer.RenderAsync(window, context);
        await renderer.WaitUntilAsync(() => window.View?.Controller is not null);
        TestProvider provider = (TestProvider)window.View!.Controller!;
        await renderer.Dispatcher.InvokeAsync(() => provider.OpenAsync("/home/user/notes.txt"));

        Assert.Same(context, provider.SeenHostContext);
        Assert.Equal("/home/user/notes.txt", window.Opened);
        Assert.Same(context, window.ContextInCallback);
    }

    [Fact]
    public async Task The_host_shows_its_unavailable_content_without_a_provider()
    {
        MinimalAppExecutionContext context = new(WindowManifest());
        using ContractRenderer renderer = new(new StubComponentHost(null));
        HostWindow window = new();

        await renderer.RenderAsync(window, context);
        await renderer.WaitUntilAsync(() => window.UnavailableReason is not null);

        Assert.Contains(Contract, window.UnavailableReason, StringComparison.Ordinal);
        Assert.Null(window.View!.Controller);
    }

    private static AppManifest WindowManifest() => new()
    {
        Id = "org.hackeros.test-contract-host",
        Name = "Contract Host",
        Version = "1.0.0",
        PublisherId = "org.hackeros",
        Description = "Typed contract host test.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.AppSdk.Blazor.Tests", "HostWindow"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None
    };

    /// <summary>The contract's controller, as an SDK would declare it.</summary>
    public interface ITestController
    {
        string? Path { get; }
    }

    /// <summary>A typed host, as the SDK's <c>HackFileView</c> is.</summary>
    public sealed class TestView : HackContractComponent<ITestController>
    {
        [Parameter]
        public string? Path { get; set; }

        [Parameter]
        public EventCallback<string> Opened { get; set; }

        protected override string Contract => HackContractComponentTests.Contract;

        protected override void AddProviderParameters(IDictionary<string, object?> parameters)
        {
            parameters[nameof(Path)] = Path;
            parameters[nameof(Opened)] = Opened;
            parameters["NewerOnly"] = true;
        }
    }

    /// <summary>The provider an OS app would register for the contract.</summary>
    public sealed class TestProvider : HackEmbeddableComponent, ITestController
    {
        [Parameter]
        public string? Path { get; set; }

        [Parameter]
        public EventCallback<string> Opened { get; set; }

        public IAppExecutionContext? SeenHostContext { get; private set; }

        public Task OpenAsync(string path) => Opened.InvokeAsync(path);

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                SeenHostContext = HostContext;
                await ControllerReady.InvokeAsync(this);
            }
        }
    }

    /// <summary>The host app's window: cascades its context, as the window manager does, and uses the typed host.</summary>
    private sealed class HostWindow : HackComponentBase
    {
        [Parameter]
        public IAppExecutionContext Context { get; set; } = null!;

        public TestView? View { get; private set; }
        public ITestController? ControllerSeen { get; private set; }
        public string? Opened { get; private set; }
        public IAppExecutionContext? ContextInCallback { get; private set; }
        public string? UnavailableReason { get; private set; }

        // A window app's own context, as WindowAppBase receives it: its event handlers run in it.
        protected override void OnParametersSet() => AppContext = Context;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<IAppExecutionContext>>(0);
            builder.AddComponentParameter(1, "Value", Context);
            builder.AddComponentParameter(2, "IsFixed", true);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(child =>
            {
                child.OpenComponent<TestView>(0);
                child.AddComponentParameter(1, nameof(TestView.Path), "/home/user");
                child.AddComponentParameter(2, nameof(TestView.Opened), EventCallback.Factory.Create<string>(this, path =>
                {
                    Opened = path;
                    ContextInCallback = HackExecutionContext.CurrentOrNull;
                }));
                child.AddComponentParameter(3, nameof(TestView.ControllerChanged), EventCallback.Factory.Create<ITestController?>(this, controller => ControllerSeen = controller));
                child.AddComponentParameter(4, nameof(TestView.Unavailable), (RenderFragment<string>)(reason => _ => UnavailableReason = reason));
                child.AddComponentReferenceCapture(5, component => View = (TestView)component);
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>Resolves the contract to <paramref name="provider"/>, or to nothing.</summary>
    private sealed class StubComponentHost(Type? provider) : IHackComponentHost
    {
        public Task<HackComponentResolution> ResolveAsync(string contract, CancellationToken cancellationToken = default) =>
            Task.FromResult(provider is null
                ? HackComponentResolution.Unavailable(contract, $"No installed app provides '{contract}'.")
                : HackComponentResolution.Resolved(contract, provider, "org.hackeros.provider"));
    }

    /// <summary>Attaches components to a real render handle, discarding render batches (no bUnit in this solution).</summary>
    private sealed class ContractRenderer(IHackComponentHost host) : Renderer(new Services(host), NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        /// <summary>Renders a host window whose <c>Context</c> parameter receives <paramref name="context"/>.</summary>
        public Task RenderAsync(IComponent window, IAppExecutionContext context) =>
            Dispatcher.InvokeAsync(async () =>
            {
                int id = AssignRootComponentId(window);
                await RenderRootComponentAsync(id, ParameterView.FromDictionary(new Dictionary<string, object?> { ["Context"] = context }));
            });

        /// <summary>Waits for the asynchronous resolution and after-render work to settle.</summary>
        public async Task WaitUntilAsync(Func<bool> condition)
        {
            for (int attempt = 0; attempt < 200 && !await Dispatcher.InvokeAsync(condition); attempt++)
            {
                await Task.Delay(10);
            }

            Assert.True(await Dispatcher.InvokeAsync(condition));
        }

        protected override void HandleException(Exception exception) => ExceptionDispatchInfo.Capture(exception).Throw();

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }

    private sealed class Services(IHackComponentHost host) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IHackComponentHost) ? host
            : serviceType == typeof(IServiceProvider) ? this
            : null;
    }
}
