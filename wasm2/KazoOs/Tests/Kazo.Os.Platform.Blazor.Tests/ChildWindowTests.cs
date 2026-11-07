using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Platform.Blazor.Shell;
using Kazo.Os.Platform.Blazor.Tests.Shell;
using Kazo.Os.Platform.Blazor.Windows;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Tests.Support;
using Kazo.Os.Windowing.Abstractions;
using Kazo.Os.Windowing.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Kazo.Os.Platform.Blazor.Tests;

/// <summary>ADR 0234 §3: an app opens child windows and dialogs owned by its window.</summary>
public sealed class ChildWindowTests
{
    [Fact]
    public async Task A_child_window_belongs_to_the_app_and_is_centered_on_its_owner()
    {
        (WindowRuntime runtime, IAppChildWindowGateway gateway, WindowRuntimeState owner) = await LaunchAsync();

        IKazoWindow child = await OpenAsync(gateway, new KazoWindowRequest
        {
            Title = "Properties",
            Options = new WindowOptions { DefaultWidth = 300, DefaultHeight = 200, MinWidth = 200, MinHeight = 150, Style = WindowStyle.Tool },
        });

        WindowRuntimeState window = runtime.Windows.Single(item => item.Id.ToString() == child.Id);
        Assert.True(child.IsOpen);
        Assert.Equal(owner.AppId, window.AppId);
        Assert.Equal(owner.OwnerInstanceId, window.OwnerInstanceId);
        Assert.Equal(owner.Id, window.OwnerId);
        Assert.Equal("Properties", window.Title);
        Assert.Equal(WindowModality.Modeless, window.Modality);
        Assert.Equal(WindowFrameStyle.Tool, window.Frame.Style);
        Assert.Equal(owner.Bounds.X + ((owner.Bounds.Width - 300) / 2), window.Bounds.X);
        Assert.Equal(owner.Bounds.Y + ((owner.Bounds.Height - 200) / 2), window.Bounds.Y);
        Assert.True(window.IsFocused);
        Assert.False(runtime.IsInteractionBlocked(owner.Id));
    }

    [Fact]
    public async Task A_modal_child_blocks_its_owner_until_it_closes_with_its_result()
    {
        (WindowRuntime runtime, IAppChildWindowGateway gateway, WindowRuntimeState owner) = await LaunchAsync();
        IKazoWindow dialog = await OpenAsync(gateway, new KazoWindowRequest { Options = new WindowOptions { Modal = true } });

        Assert.True(runtime.IsInteractionBlocked(owner.Id));
        Assert.Equal(dialog.Id, runtime.ActivationTarget(owner.Id).ToString());
        Assert.True(await dialog.CloseAsync(KazoDialogResult.Ok("42")));

        KazoDialogResult result = await dialog.Closed;
        Assert.True(result.IsAccepted);
        Assert.Equal("42", result.ValueAs<string>());
        Assert.False(dialog.IsOpen);
        Assert.False(runtime.IsInteractionBlocked(owner.Id));
        Assert.False(await dialog.CloseAsync());
    }

    [Fact]
    public async Task The_close_button_cancels()
    {
        (WindowRuntime runtime, IAppChildWindowGateway gateway, _) = await LaunchAsync();
        IKazoWindow dialog = await OpenAsync(gateway, new KazoWindowRequest { Options = new WindowOptions { Modal = true } });
        WindowRuntimeState window = runtime.Windows.Single(item => item.Id.ToString() == dialog.Id);

        await window.OnRequestClose!();

        Assert.Equal(KazoDialogResultKind.Cancel, (await dialog.Closed).Kind);
    }

    [Fact]
    public async Task A_child_closes_with_its_owner_without_a_result()
    {
        (WindowRuntime runtime, IAppChildWindowGateway gateway, WindowRuntimeState owner) = await LaunchAsync();
        IKazoWindow child = await OpenAsync(gateway, new KazoWindowRequest());

        runtime.Apply(new ForceWindowCloseCommand(owner.Id));
        KazoDialogResult result = await child.Closed.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(KazoDialogResultKind.None, result.Kind);
        Assert.Empty(runtime.Windows);
    }

    [Fact]
    public async Task The_child_renders_its_component_with_its_parameters_and_its_own_handle()
    {
        (WindowRuntime runtime, IAppChildWindowGateway gateway, _) = await LaunchAsync();
        IKazoWindow child = await OpenAsync(gateway, new KazoWindowRequest
        {
            Title = "Rename",
            Parameters = new Dictionary<string, object?> { [nameof(ProbeWindow.Name)] = "photo.png" },
        });
        WindowRuntimeState window = runtime.Windows.Single(item => item.Id.ToString() == child.Id);
        ProbeWindow.Last = null;

        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        FrameCapturingRenderer renderer = new(services);
        await renderer.RenderAsync(new FragmentHost(window.Content!));

        ProbeWindow probe = Assert.IsType<ProbeWindow>(ProbeWindow.Last);
        Assert.Equal("photo.png", probe.Name);
        Assert.Equal("Rename", probe.Title);
        Assert.Equal("org.kazoos.test-child", probe.AppId);
        Assert.True(await renderer.Dispatcher.InvokeAsync(() => probe.CloseWithAsync(KazoDialogResult.Ok("photo2.png")).AsTask()));
        Assert.Equal("photo2.png", (await child.Closed).ValueAs<string>());
    }

    [Fact]
    public async Task A_gateway_without_an_app_context_or_a_closed_owner_opens_nothing()
    {
        (WindowRuntime runtime, _, WindowRuntimeState owner) = await LaunchAsync();
        IAppChildWindowGateway noContext = (IAppChildWindowGateway)new AppWindowGatewayFactory(runtime).Create(owner.Id, CreateManifest());

        Assert.Null(await noContext.OpenWindowAsync(typeof(ProbeWindow), new KazoWindowRequest()));
        await Assert.ThrowsAsync<ArgumentException>(() => noContext.OpenWindowAsync(typeof(FragmentHost), new KazoWindowRequest()).AsTask());
    }

    private static async Task<IKazoWindow> OpenAsync(IAppChildWindowGateway gateway, KazoWindowRequest request) =>
        await gateway.OpenWindowAsync(typeof(ProbeWindow), request) ?? throw new InvalidOperationException("No window.");

    private static async Task<(WindowRuntime Runtime, IAppChildWindowGateway Gateway, WindowRuntimeState Owner)> LaunchAsync()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowLaunchCoordinator coordinator = new(runtime);
        AppManifest manifest = CreateManifest();
        await coordinator.PresentAsync(manifest, CreateProcess());
        WindowRuntimeState owner = Assert.Single(runtime.Windows);
        IAppWindowGateway gateway = new AppWindowGatewayFactory(runtime, coordinator)
            .Create(owner.Id, new MinimalAppExecutionContext(manifest));
        return (runtime, Assert.IsAssignableFrom<IAppChildWindowGateway>(gateway), owner);
    }

    private static AppManifest CreateManifest() => new()
    {
        Id = "org.kazoos.test-child",
        Name = "Test Child",
        Version = "1.0.0",
        PublisherId = "pub.kazoos",
        Description = "Child window test app.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Test.dll", "Test.Window"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Window = new WindowOptions { DefaultWidth = 800, DefaultHeight = 600 },
    };

    private static ProcessRecord CreateProcess()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new ProcessRecord(
            ProcessId.FromInt64(44),
            parentPid: null,
            "org.kazoos.test-child",
            AppInstanceId.FromGuid(Guid.NewGuid()),
            AppKind.Window,
            LocalUserId.FromGuid(Guid.NewGuid()),
            SessionId.FromGuid(Guid.NewGuid()),
            ProcessState.Running,
            ResourceProfile.None,
            serviceHealth: null,
            now,
            startedAtUtc: now,
            stoppedAtUtc: null,
            exitCode: null,
            exitReason: null);
    }

    /// <summary>A child window that records what it received.</summary>
    public sealed class ProbeWindow : KazoWindowBase
    {
        public static ProbeWindow? Last { get; set; }

        [Parameter]
        public string? Name { get; set; }

        public string Title => WindowTitle;

        public string? AppId => AppContext?.Manifest.Id;

        public ValueTask<bool> CloseWithAsync(KazoDialogResult result) => CloseAsync(result);

        protected override void OnInitialized() => Last = this;
    }

    /// <summary>Renders a window's content fragment.</summary>
    private sealed class FragmentHost(RenderFragment content) : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, content);
    }
}
