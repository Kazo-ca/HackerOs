using HackerOs.AppSdk.Blazor.ContextMenus;
using HackerOs.Platform.Blazor.ContextMenus;
using HackerOs.Platform.Blazor.Display;
using HackerOs.Platform.Blazor.Tests.Localization;
using HackerOs.Platform.Blazor.Windows;
using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Blazor;
using HackerOs.Windowing.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace HackerOs.Platform.Blazor.Tests;

/// <summary>
/// ADR 0179 §3-6: the window menus open in the OS context menu, with « Autre écran ▸ », and the
/// highlighted « Écran N » shows the identification veil.
/// </summary>
public sealed class WindowMenuContextMenuTests
{
    private static readonly WindowBounds Area = new(0, 0, 1280, 720);

    [Fact]
    public async Task The_window_menu_opens_in_the_os_context_menu_with_the_other_screen_submenu()
    {
        await using Harness harness = await Harness.StartAsync(screens: 2);

        ContextMenuSession session = await harness.OpenAsync(WindowMenuKind.Window);

        Assert.Equal(WindowManagerOverlays.WindowMenuName, session.Name);
        Assert.Equal(
            ["Maximize", "Minimize", "Move", "Resize", "Snap left", "Snap right", "Full screen (Linux keys)", "Close", null, "Other screen"],
            session.Root.Rows.Select(row => row.Item?.Title));
        ContextMenuLevel screens = session.Root.Rows[^1].Submenu!;
        Assert.Equal(["Screen 1", "Screen 2", null, "New screen"], screens.Rows.Select(row => row.Item?.Title));
        Assert.True(screens.Rows[0].Item!.IsDisabled);
        Assert.False(screens.Rows[1].Item!.IsDisabled);
        Assert.True(screens.Rows[3].Item!.IsDisabled);
    }

    [Fact]
    public async Task The_maximize_button_menu_has_snap_full_screen_and_other_screen()
    {
        await using Harness harness = await Harness.StartAsync(screens: 1);

        ContextMenuSession session = await harness.OpenAsync(WindowMenuKind.MaximizeButton);

        Assert.Equal(WindowManagerOverlays.MaximizeMenuName, session.Name);
        Assert.Equal(
            ["Snap left", "Snap right", "Full screen (Linux keys)", null, "Other screen"],
            session.Root.Rows.Select(row => row.Item?.Title));
    }

    [Fact]
    public async Task Choosing_a_screen_moves_the_window_and_closes_both_menus()
    {
        await using Harness harness = await Harness.StartAsync(screens: 2);
        ContextMenuSession session = await harness.OpenAsync(WindowMenuKind.Window);
        HackMenuItemDefinition screen2 = session.Root.Rows[^1].Submenu!.Rows[1].Item!;

        await harness.Dispatch(() => harness.ContextMenus.InvokeAsync(screen2));

        Assert.Null(harness.ContextMenus.Current);
        Assert.Null(harness.Commands.Menu);
        Assert.Equal(DisplayId.FromNumber(2), harness.Runtime.Windows.Single().DisplayId);
    }

    [Fact]
    public async Task Closing_the_context_menu_closes_the_window_menu()
    {
        await using Harness harness = await Harness.StartAsync(screens: 1);
        await harness.OpenAsync(WindowMenuKind.Window);

        await harness.Dispatch(() =>
        {
            harness.ContextMenus.Close();
            return Task.CompletedTask;
        });

        Assert.Null(harness.Commands.Menu);
    }

    [Fact]
    public async Task A_highlighted_screen_entry_shows_each_screen_its_number_and_marks_the_target()
    {
        await using Harness harness = await Harness.StartAsync(screens: 2);
        ContextMenuSession session = await harness.OpenAsync(WindowMenuKind.Window);
        HackMenuItemDefinition screen2 = session.Root.Rows[^1].Submenu!.Rows[1].Item!;

        Assert.DoesNotContain("screen-identify-veil", await harness.RenderVeilAsync(1), StringComparison.Ordinal);

        await harness.Dispatch(() =>
        {
            harness.ContextMenus.SetHighlighted(screen2);
            return Task.CompletedTask;
        });

        string first = await harness.RenderVeilAsync(1);
        string second = await harness.RenderVeilAsync(2);
        Assert.Contains("data-screen-identify=\"1\"", first, StringComparison.Ordinal);
        Assert.DoesNotContain("is-target", first, StringComparison.Ordinal);
        Assert.Contains("screen-identify-veil is-target", second, StringComparison.Ordinal);
        Assert.Contains(">2</div>", second, StringComparison.Ordinal);

        await harness.Dispatch(() =>
        {
            harness.ContextMenus.Close();
            return Task.CompletedTask;
        });

        Assert.Null(harness.ContextMenus.Highlighted);
        Assert.DoesNotContain("screen-identify-veil", await harness.RenderVeilAsync(2), StringComparison.Ordinal);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;

        private Harness(ServiceProvider provider)
        {
            _provider = provider;
            _renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        }

        public WindowRuntime Runtime => _provider.GetRequiredService<WindowRuntime>();

        public WindowKeyboardCommands Commands => _provider.GetRequiredService<WindowKeyboardCommands>();

        public ContextMenuService ContextMenus => _provider.GetRequiredService<ContextMenuService>();

        public WindowId WindowId { get; private set; }

        public static async Task<Harness> StartAsync(int screens)
        {
            ServiceCollection services = new();
            services.AddEnglishLocalizer();
            services.AddSingleton<IJSRuntime, SilentJsRuntime>();
            WindowRuntime runtime = new(Area);
            for (int number = 2; number <= screens; number++)
            {
                runtime.Apply(new AddDisplayCommand(DisplayId.FromNumber(number), Area));
            }

            services.AddSingleton(runtime);
            services.AddSingleton(new WindowKeyboardCommands(runtime));
            services.AddSingleton(new ContextMenuService());
            Harness harness = new(services.BuildServiceProvider());
            WindowRuntimeState state = new(
                WindowId.FromGuid(Guid.Parse("71000000-0000-0000-0000-000000000001")),
                "org.hackeros.test",
                WindowOwnerId.FromGuid(Guid.Parse("81000000-0000-0000-0000-000000000001")),
                "Test",
                null,
                new WindowBounds(40, 40, 640, 480),
                null,
                0,
                WindowVisualState.Normal,
                new WindowConstraints(true, 320, 240));
            runtime.Apply(new CreateWindowCommand(state));
            harness.WindowId = state.Id;
            await harness._renderer.Dispatcher.InvokeAsync(() => harness._renderer.RenderComponentAsync<WindowManagerOverlays>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(WindowManagerOverlays.Commands)] = harness.Commands })));
            return harness;
        }

        public async Task<ContextMenuSession> OpenAsync(WindowMenuKind kind)
        {
            await Dispatch(() =>
            {
                Commands.OpenMenu(WindowId, 100, 100, kind);
                return Task.CompletedTask;
            });
            for (int attempt = 0; attempt < 100 && ContextMenus.Current is null; attempt++)
            {
                await Task.Delay(10);
            }

            return ContextMenus.Current ?? throw new InvalidOperationException("The window menu did not open.");
        }

        public Task Dispatch(Func<Task> work) => _renderer.Dispatcher.InvokeAsync(work);

        public async Task<string> RenderVeilAsync(int number)
        {
            await using HtmlRenderer renderer = new(_provider, NullLoggerFactory.Instance);
            return await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<ScreenIdentifyVeil>(ParameterView.FromDictionary(
                    new Dictionary<string, object?> { [nameof(ScreenIdentifyVeil.Number)] = number }))).ToHtmlString());
        }

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }

    private sealed class SilentJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("No browser in this test.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new InvalidOperationException("No browser in this test.");
    }
}
