using System.Globalization;
using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace HackerOs.Platform.Blazor.Tests;

/// <summary>Protects the numeric boundary between localized Razor markup and pointer interop.</summary>
public sealed class WindowPointerGeometryTests
{
    [Theory]
    [InlineData("fr-FR")]
    [InlineData("fr-CA")]
    [InlineData("en-US")]
    public async Task Fractional_geometry_is_rendered_as_invariant_browser_numbers(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            ServiceCollection services = new();
            services.AddSingleton<IJSRuntime, NoJsRuntime>();
            await using ServiceProvider provider = services.BuildServiceProvider();
            await using HtmlRenderer renderer = new(provider, NullLoggerFactory.Instance);
            string html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var rendered = await renderer.RenderComponentAsync<WindowHost>(ParameterView.FromDictionary(
                    new Dictionary<string, object?> { [nameof(WindowHost.Window)] = Window() }));
                return rendered.ToHtmlString();
            });

            Assert.Contains("data-window-x=\"48.25\"", html, StringComparison.Ordinal);
            Assert.Contains("data-window-y=\"42.5\"", html, StringComparison.Ordinal);
            Assert.Contains("data-window-width=\"640.75\"", html, StringComparison.Ordinal);
            Assert.Contains("data-window-height=\"480.5\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("style=", html, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task Non_finite_pointer_values_never_reach_the_runtime(double invalid)
    {
        List<WindowCommand> commands = [];
        WindowChrome chrome = new()
        {
            Window = Window(),
            OnGesture = EventCallback.Factory.Create<WindowCommand>(this, commands.Add),
        };
        await chrome.ReportPointerDeltaAsync("move", null, invalid, 0, 48.25, 42.5, 640.75, 480.5);
        await chrome.ReportPointerDeltaAsync("resize", "right", 10, 0, 48.25, 42.5, invalid, 480.5);
        Assert.Empty(commands);
    }

    [Fact]
    public async Task A_window_dropped_on_another_screen_changes_screen_then_takes_the_dropped_position()
    {
        List<WindowCommand> commands = [];
        WindowRuntimeState window = Window();
        WindowChrome chrome = new()
        {
            Window = window,
            OnGesture = EventCallback.Factory.Create<WindowCommand>(this, commands.Add),
        };

        await chrome.ReportScreenDropAsync(2, 120.5, 64);

        Assert.Equal(
            [new MoveWindowToDisplayCommand(window.Id, DisplayId.FromNumber(2)), new MoveWindowCommand(window.Id, 120.5, 64)],
            commands);
    }

    [Theory]
    [InlineData(0, 10, 10)]
    [InlineData(2, double.NaN, 10)]
    [InlineData(2, 10, double.PositiveInfinity)]
    public async Task An_invalid_screen_drop_never_reaches_the_runtime(int display, double x, double y)
    {
        List<WindowCommand> commands = [];
        WindowChrome chrome = new()
        {
            Window = Window(),
            OnGesture = EventCallback.Factory.Create<WindowCommand>(this, commands.Add),
        };

        await chrome.ReportScreenDropAsync(display, x, y);

        Assert.Empty(commands);
    }

    [Fact]
    public async Task A_maximized_window_is_never_dropped_on_another_screen()
    {
        List<WindowCommand> commands = [];
        WindowChrome chrome = new()
        {
            Window = Window(WindowVisualState.Maximized),
            OnGesture = EventCallback.Factory.Create<WindowCommand>(this, commands.Add),
        };

        await chrome.ReportScreenDropAsync(2, 10, 10);

        Assert.Empty(commands);
    }

    private static WindowRuntimeState Window(WindowVisualState state = WindowVisualState.Normal) => new(
        WindowId.FromGuid(Guid.NewGuid()), "org.hackeros.test", WindowOwnerId.FromGuid(Guid.NewGuid()),
        "Test window", null, new WindowBounds(48.25, 42.5, 640.75, 480.5), null, 1,
        state, new WindowConstraints(true, 320, 240));

    /// <summary>Static rendering never imports the interactive pointer module.</summary>
    private sealed class NoJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new NotSupportedException();
    }
}
