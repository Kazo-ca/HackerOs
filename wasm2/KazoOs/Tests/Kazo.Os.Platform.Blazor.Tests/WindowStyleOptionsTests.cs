using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Platform.Blazor.Shell;
using Kazo.Os.Platform.Blazor.Tests.Shell;
using Kazo.Os.Platform.Blazor.Windows;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Windowing.Abstractions;
using Kazo.Os.Windowing.Blazor;
using Kazo.Os.Windowing.Core;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Kazo.Os.Platform.Blazor.Tests;

/// <summary>
/// ADR 0234 §1, §2 and §4: the manifest's window style and switches reach the frame, the taskbar and
/// the chrome, and an app drives its own window through its gateway (issue #384).
/// </summary>
public sealed class WindowStyleOptionsTests
{
    [Fact]
    public async Task The_manifest_style_and_switches_reach_the_new_window()
    {
        (WindowRuntime runtime, _, _) = await LaunchAsync(new WindowOptions
        {
            Style = WindowStyle.Tool,
            ShowIcon = false,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
        });

        WindowRuntimeState window = Assert.Single(runtime.Windows);
        Assert.Equal(
            new WindowFrame(WindowFrameStyle.Tool, ShowIcon: false, MaximizeBox: false, MinimizeBox: false, ShowInTaskbar: false),
            window.Frame);
    }

    [Fact]
    public async Task An_app_that_declares_nothing_keeps_the_default_frame()
    {
        (WindowRuntime runtime, _, _) = await LaunchAsync(window: null);

        Assert.Equal(WindowFrame.Default, Assert.Single(runtime.Windows).Frame);
    }

    [Fact]
    public async Task A_window_without_a_taskbar_button_is_left_out_of_the_taskbar()
    {
        (WindowRuntime runtime, _, _) = await LaunchAsync(new WindowOptions { ShowInTaskbar = false });
        using TaskbarWindowSourceAdapter taskbar = new(runtime);

        Assert.Empty(taskbar.Windows);
        Assert.Single(runtime.Windows);
    }

    [Fact]
    public async Task The_gateway_minimizes_maximizes_and_restores_its_window_whatever_the_buttons()
    {
        // ADR 0234 §4: MinimizeBox = false hides the button from the user, not from the app (#384).
        (WindowRuntime runtime, IAppWindowGateway gateway, _) = await LaunchAsync(new WindowOptions
        {
            Resizable = true,
            MinimizeBox = false,
            MaximizeBox = false,
        });

        Assert.True(await gateway.MinimizeAsync());
        Assert.Equal(WindowVisualState.Minimized, Assert.Single(runtime.Windows).VisualState);
        Assert.True(await gateway.MinimizeAsync());
        Assert.True(await gateway.RestoreAsync());
        Assert.Equal(WindowVisualState.Normal, Assert.Single(runtime.Windows).VisualState);
        Assert.True(await gateway.MaximizeAsync());
        Assert.Equal(WindowVisualState.Maximized, Assert.Single(runtime.Windows).VisualState);
        Assert.True(await gateway.RestoreAsync());
        Assert.True(await gateway.RestoreAsync());
    }

    [Fact]
    public async Task The_gateway_moves_and_resizes_within_the_declared_sizes()
    {
        (WindowRuntime runtime, IAppWindowGateway gateway, _) = await LaunchAsync(new WindowOptions
        {
            Resizable = true,
            MinWidth = 300,
            MinHeight = 200,
            MaxWidth = 700,
            MaxHeight = 500,
        });

        Assert.True(await gateway.MoveAsync(100, 50));
        Assert.True(await gateway.ResizeAsync(2000, 100));

        WindowBounds bounds = Assert.Single(runtime.Windows).Bounds;
        Assert.Equal(100, bounds.X);
        Assert.Equal(50, bounds.Y);
        Assert.Equal(700, bounds.Width);
        Assert.Equal(200, bounds.Height);
        Assert.False(await gateway.MoveAsync(double.NaN, 0));
        Assert.False(await gateway.ResizeAsync(0, 100));
    }

    [Fact]
    public async Task A_fixed_size_window_refuses_maximize_and_resize_without_throwing()
    {
        (WindowRuntime runtime, IAppWindowGateway gateway, _) = await LaunchAsync(new WindowOptions { Resizable = false });

        Assert.False(await gateway.MaximizeAsync());
        Assert.False(await gateway.ResizeAsync(500, 400));
        Assert.Equal(WindowVisualState.Normal, Assert.Single(runtime.Windows).VisualState);
    }

    [Fact]
    public async Task The_gateway_changes_style_and_switches_at_run_time()
    {
        (WindowRuntime runtime, IAppWindowGateway gateway, WindowOptions declared) = await LaunchAsync(new WindowOptions { Resizable = true });
        WindowOptions frameless = declared with { Style = WindowStyle.None, ShowInTaskbar = false };

        Assert.Equal(declared, gateway.Options);
        Assert.True(await gateway.SetOptionsAsync(frameless));

        Assert.Equal(frameless, gateway.Options);
        Assert.Equal(WindowFrameStyle.None, Assert.Single(runtime.Windows).Frame.Style);
        Assert.False(Assert.Single(runtime.Windows).Frame.ShowInTaskbar);
    }

    [Fact]
    public async Task Requests_on_a_closed_window_return_false()
    {
        (WindowRuntime runtime, IAppWindowGateway gateway, WindowOptions declared) = await LaunchAsync(new WindowOptions { Resizable = true });
        runtime.Apply(new ForceWindowCloseCommand(Assert.Single(runtime.Windows).Id));

        Assert.False(await gateway.MinimizeAsync());
        Assert.False(await gateway.MaximizeAsync());
        Assert.False(await gateway.RestoreAsync());
        Assert.False(await gateway.MoveAsync(1, 1));
        Assert.False(await gateway.ResizeAsync(400, 300));
        Assert.False(await gateway.SetOptionsAsync(declared with { Style = WindowStyle.Tool }));
        Assert.Equal(declared, gateway.Options);
    }

    [Theory]
    [InlineData(WindowFrameStyle.Normal, true, true, true, true, 8)]
    [InlineData(WindowFrameStyle.Tool, true, false, false, false, 0)]
    [InlineData(WindowFrameStyle.None, false, false, false, false, 0)]
    public async Task The_chrome_draws_what_the_style_allows(
        WindowFrameStyle style,
        bool close,
        bool icon,
        bool minimize,
        bool maximize,
        int handles)
    {
        IReadOnlyList<string> classes = await RenderChromeAsync(new WindowFrame(style));

        Assert.Equal(close, classes.Any(value => value.Contains("chrome-button-close", StringComparison.Ordinal)));
        Assert.Equal(icon, classes.Any(value => value == "window-icon"));
        Assert.Equal(minimize, classes.Any(value => value.Contains("chrome-button-minimize", StringComparison.Ordinal)));
        Assert.Equal(maximize, classes.Any(value => value.Contains("chrome-button-maximize", StringComparison.Ordinal)));
        Assert.Equal(handles, classes.Count(value => value.StartsWith("resize-handle", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_switches_remove_their_buttons_from_a_normal_window()
    {
        IReadOnlyList<string> classes = await RenderChromeAsync(new WindowFrame(ShowIcon: false, MaximizeBox: false, ShowInTaskbar: false));

        Assert.DoesNotContain(classes, value => value == "window-icon");
        Assert.DoesNotContain(classes, value => value.Contains("chrome-button-minimize", StringComparison.Ordinal));
        Assert.DoesNotContain(classes, value => value.Contains("chrome-button-maximize", StringComparison.Ordinal));
        Assert.Contains(classes, value => value.Contains("chrome-button-close", StringComparison.Ordinal));
        Assert.Equal(8, classes.Count(value => value.StartsWith("resize-handle", StringComparison.Ordinal)));
    }

    private static async Task<IReadOnlyList<string>> RenderChromeAsync(WindowFrame frame)
    {
        WindowRuntimeState window = new WindowRuntimeState(
            WindowId.FromGuid(Guid.NewGuid()),
            "org.kazoos.chrome",
            WindowOwnerId.FromGuid(Guid.NewGuid()),
            "Chrome",
            "icons/app.svg",
            new WindowBounds(10, 10, 640, 480),
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240),
            WindowModality.Modeless,
            null) with { Frame = frame };
        ServiceProvider services = new ServiceCollection()
            .AddSingleton<IJSRuntime, InertJsRuntime>()
            .BuildServiceProvider();
        await using (services)
        {
            FrameCapturingRenderer renderer = new(services);
            // The renderer instantiates the chrome itself, so its [Inject] properties are supplied.
            ArrayRange<RenderTreeFrame> hostFrames = await renderer.RenderAsync(new ChromeHost(window));
            int chromeId = hostFrames.Array[..hostFrames.Count]
                .Single(item => item.FrameType == RenderTreeFrameType.Component)
                .ComponentId;
            ArrayRange<RenderTreeFrame> frames = await renderer.Dispatcher.InvokeAsync(() => renderer.FramesOf(chromeId));
            return [.. frames.Array[..frames.Count]
                .Where(item => item.FrameType == RenderTreeFrameType.Attribute
                    && item.AttributeName == "class"
                    && item.AttributeValue is string)
                .Select(item => (string)item.AttributeValue)];
        }
    }

    private static async Task<(WindowRuntime Runtime, IAppWindowGateway Gateway, WindowOptions Options)> LaunchAsync(WindowOptions? window)
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowLaunchCoordinator coordinator = new(runtime);
        AppManifest manifest = CreateManifest() with
        {
            Window = window is null ? null : window with { DefaultWidth = 640, DefaultHeight = 480 },
        };
        await coordinator.PresentAsync(manifest, CreateProcess());
        IAppWindowGateway gateway = new AppWindowGatewayFactory(runtime, coordinator)
            .Create(Assert.Single(runtime.Windows).Id, manifest);
        return (runtime, gateway, manifest.Window ?? WindowOptions.Default);
    }

    private static AppManifest CreateManifest() => new()
    {
        Id = "org.kazoos.test-style",
        Name = "Test Style",
        Version = "1.0.0",
        PublisherId = "pub.kazoos",
        Description = "Window style test app.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Test.dll", "Test.Window"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
    };

    private static ProcessRecord CreateProcess()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new ProcessRecord(
            ProcessId.FromInt64(43),
            parentPid: null,
            "org.kazoos.test-style",
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

    /// <summary>Renders one <see cref="WindowChrome"/> for a window.</summary>
    private sealed class ChromeHost(WindowRuntimeState window) : Microsoft.AspNetCore.Components.ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<WindowChrome>(0);
            builder.AddComponentParameter(1, nameof(WindowChrome.Window), window);
            builder.AddComponentParameter(2, nameof(WindowChrome.TitleId), "chrome-title");
            builder.CloseComponent();
        }
    }

    /// <summary>JS runtime that answers every call, so the chrome's gesture module loads as a no-op.</summary>
    private sealed class InertJsRuntime : IJSRuntime, IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(typeof(TValue) == typeof(IJSObjectReference) ? (TValue)(object)this : default!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
