using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Blazor.Tests;

/// <summary>
/// ADR 0234 §4 and issue #384: a window app minimizes, maximizes, restores, moves, resizes and restyles
/// its own window through <see cref="WindowAppBase"/>.
/// </summary>
public sealed partial class WindowAppBaseWindowTitleTests
{
    [Fact]
    public async Task Window_requests_reach_the_app_s_own_gateway()
    {
        FrameGateway gateway = new();
        using TitleRenderer renderer = new();
        TitleHost host = new() { Gateway = gateway, Context = new MinimalAppExecutionContext(Manifest()) };
        await renderer.RenderAsync(host);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            TitleApp app = host.App!;
            Assert.True(await app.MinimizeAsync());
            Assert.True(await app.MaximizeAsync());
            Assert.True(await app.RestoreAsync());
            Assert.True(await app.MoveAsync(10, 20));
            Assert.True(await app.ResizeAsync(300, 200));
            Assert.True(await app.StyleAsync(WindowStyle.None));
        });

        Assert.Equal(["minimize", "maximize", "restore", "move 10,20", "resize 300x200", "options None"], gateway.Requests);
        Assert.Equal(WindowStyle.None, host.App!.Options.Style);
    }

    [Fact]
    public async Task Changing_the_style_keeps_the_other_options()
    {
        FrameGateway gateway = new() { Options = new WindowOptions { Resizable = true, MinWidth = 300, MaximizeBox = false } };
        using TitleRenderer renderer = new();
        TitleHost host = new() { Gateway = gateway, Context = new MinimalAppExecutionContext(Manifest()) };
        await renderer.RenderAsync(host);

        await renderer.Dispatcher.InvokeAsync(async () => await host.App!.StyleAsync(WindowStyle.Tool));

        Assert.Equal(new WindowOptions { Resizable = true, MinWidth = 300, MaximizeBox = false, Style = WindowStyle.Tool }, gateway.Options);
    }

    [Fact]
    public async Task A_gateway_from_before_SDK_3_1_refuses_the_requests()
    {
        // Default interface members keep an older platform's gateway compiling; the app learns it failed.
        using TitleRenderer renderer = new();
        TitleHost host = new() { Gateway = new RecordingGateway("Title App"), Context = new MinimalAppExecutionContext(Manifest()) };
        await renderer.RenderAsync(host);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Assert.False(await host.App!.MinimizeAsync());
            Assert.False(await host.App.OptionsAsync(WindowOptions.Default with { Style = WindowStyle.Tool }));
        });
        Assert.Equal(WindowOptions.Default, host.App!.Options);
    }

    [Fact]
    public async Task Without_a_window_manager_requests_return_false_and_the_manifest_options_are_read()
    {
        WindowOptions declared = new() { Resizable = false, ShowInTaskbar = false };
        TitleApp app = new() { InitialTitle = null };
        app.Bind(new MinimalAppExecutionContext(Manifest() with { Window = declared }));

        Assert.Equal(declared, app.Options);
        Assert.False(await app.MinimizeAsync());
        Assert.False(await app.MaximizeAsync());
        Assert.False(await app.RestoreAsync());
        Assert.False(await app.MoveAsync(0, 0));
        Assert.False(await app.ResizeAsync(100, 100));
        Assert.False(await app.StyleAsync(WindowStyle.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => app.OptionsAsync(null!).AsTask());
    }

    /// <summary>A platform gateway from SDK 3.1 that records what the app asks.</summary>
    private sealed class FrameGateway : IAppWindowGateway
    {
        public List<string> Requests { get; } = [];

        public string Title => "Title App";

        public string DefaultTitle => "Title App";

        public WindowOptions Options { get; set; } = WindowOptions.Default;

        public ValueTask<string> SetTitleAsync(string? title, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(title ?? DefaultTitle);

        public ValueTask<bool> SetOptionsAsync(WindowOptions options, CancellationToken cancellationToken = default)
        {
            Requests.Add($"options {options.Style}");
            Options = options;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> MinimizeAsync(CancellationToken cancellationToken = default) => Record("minimize");

        public ValueTask<bool> MaximizeAsync(CancellationToken cancellationToken = default) => Record("maximize");

        public ValueTask<bool> RestoreAsync(CancellationToken cancellationToken = default) => Record("restore");

        public ValueTask<bool> MoveAsync(double x, double y, CancellationToken cancellationToken = default) => Record($"move {x},{y}");

        public ValueTask<bool> ResizeAsync(double width, double height, CancellationToken cancellationToken = default) =>
            Record($"resize {width}x{height}");

        private ValueTask<bool> Record(string request)
        {
            Requests.Add(request);
            return ValueTask.FromResult(true);
        }
    }
}
