using HackerOs.AppSdk.Blazor.ContextMenus;
using HackerOs.Platform.Blazor.ContextMenus;
using HackerOs.Platform.Blazor.Shell;
using HackerOs.Platform.Blazor.Tests.Localization;
using HackerOs.Platform.Blazor.Windows;
using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace HackerOs.Platform.Blazor.Tests.Shell;

/// <summary>
/// ADR 0194 §2-4: a window's taskbar button opens « Nouvelle fenêtre » and the window menu; the rest
/// of each screen's taskbar opens the taskbar menu, whose entries act on that screen.
/// </summary>
public sealed class TaskbarContextMenusTests
{
    private static readonly WindowBounds Area = new(0, 0, 1200, 800);
    private static readonly DisplayId Screen2 = DisplayId.FromNumber(2);
    private static readonly WindowId Notes = WindowId.FromGuid(Guid.Parse("c1000000-0000-0000-0000-000000000001"));

    [Fact]
    public async Task A_window_button_opens_new_window_then_the_window_menu_and_other_screen()
    {
        using Harness harness = new();

        ContextMenuSession session = await harness.OpenAsync(harness.Menus.For(DisplayId.Primary).WindowItemAttributes(Notes));

        Assert.Equal(TaskbarContextMenus.WindowMenuName, session.Name);
        Assert.Equal(
            ["New window", null, "Maximize", "Minimize", "Move", "Resize", "Snap left", "Snap right", "Full screen (Linux keys)", "Close", null, "Other screen"],
            session.Root.Rows.Select(row => row.Item?.Title));
        Assert.False(session.Root.Rows[0].Item!.IsDisabled);
    }

    [Fact]
    public async Task New_window_launches_the_app_again_on_the_screen_of_the_taskbar()
    {
        using Harness harness = new();
        ContextMenuSession session = await harness.OpenAsync(harness.Menus.For(Screen2).WindowItemAttributes(Notes));

        await harness.ContextMenus.InvokeAsync(session.Root.Rows[0].Item!);

        Assert.Equal([("org.hackeros.notes", "", Screen2)], harness.Launches);
    }

    [Fact]
    public async Task New_window_is_disabled_for_an_app_that_accepts_one_window()
    {
        using Harness harness = new(canOpenNewWindow: false);

        ContextMenuSession session = await harness.OpenAsync(harness.Menus.For(DisplayId.Primary).WindowItemAttributes(Notes));

        Assert.True(session.Root.Rows[0].Item!.IsDisabled);
    }

    [Fact]
    public async Task The_taskbar_opens_task_manager_window_arrangement_and_settings()
    {
        using Harness harness = new();

        ContextMenuSession session = await harness.OpenAsync(harness.Menus.For(DisplayId.Primary).TaskbarAttributes);

        Assert.Equal(TaskbarContextMenus.TaskbarMenuName, session.Name);
        Assert.Equal(
            ["Task manager", null, "Show the desktop", "Cascade windows", "Tile windows", null, "Taskbar settings"],
            session.Root.Rows.Select(row => row.Item?.Title));
        Assert.All(session.Root.Rows.Where(row => row.Item is not null), row => Assert.False(row.Item!.IsDisabled));
    }

    [Fact]
    public async Task Show_desktop_acts_on_the_screen_of_the_taskbar_only()
    {
        using Harness harness = new();
        ContextMenuSession session = await harness.OpenAsync(harness.Menus.For(Screen2).TaskbarAttributes);

        // Screen 2 has no window: nothing to show the desktop of.
        Assert.True(Entry(session, TaskbarContextMenus.ShowDesktopItemId).IsDisabled);

        session = await harness.OpenAsync(harness.Menus.For(DisplayId.Primary).TaskbarAttributes);
        await harness.ContextMenus.InvokeAsync(Entry(session, TaskbarContextMenus.ShowDesktopItemId));

        Assert.Equal(WindowVisualState.Minimized, harness.Runtime.Windows.Single().VisualState);
    }

    [Fact]
    public async Task Task_manager_and_settings_open_their_apps_on_the_screen_of_the_taskbar()
    {
        using Harness harness = new();
        ContextMenuSession session = await harness.OpenAsync(harness.Menus.For(Screen2).TaskbarAttributes);
        await harness.ContextMenus.InvokeAsync(Entry(session, TaskbarContextMenus.TaskManagerItemId));
        session = await harness.OpenAsync(harness.Menus.For(Screen2).TaskbarAttributes);
        await harness.ContextMenus.InvokeAsync(Entry(session, TaskbarContextMenus.SettingsItemId));

        Assert.Equal(
            [
                (TaskbarContextMenus.TaskManagerAppId, "", Screen2),
                (TaskbarContextMenus.SettingsAppId, TaskbarContextMenus.SettingsPageArgument, Screen2),
            ],
            harness.Launches);
    }

    [Fact]
    public async Task Apps_missing_from_the_catalog_disable_their_entries()
    {
        using Harness harness = new(catalog: []);

        ContextMenuSession session = await harness.OpenAsync(harness.Menus.For(DisplayId.Primary).TaskbarAttributes);

        Assert.True(Entry(session, TaskbarContextMenus.TaskManagerItemId).IsDisabled);
        Assert.True(Entry(session, TaskbarContextMenus.SettingsItemId).IsDisabled);
    }

    private static HackMenuItemDefinition Entry(ContextMenuSession session, string id) =>
        session.Root.Rows.Select(row => row.Item).OfType<HackMenuItemDefinition>().Single(item => item.Id == id);

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _provider;

        public Harness(bool canOpenNewWindow = true, string[]? catalog = null)
        {
            ServiceCollection services = new();
            services.AddEnglishLocalizer();
            _provider = services.BuildServiceProvider();
            Runtime = new WindowRuntime(Area);
            Runtime.Apply(new AddDisplayCommand(Screen2, Area));
            Runtime.Apply(new CreateWindowCommand(new WindowRuntimeState(
                Notes,
                "org.hackeros.notes",
                WindowOwnerId.FromGuid(Guid.Parse("d1000000-0000-0000-0000-000000000001")),
                "Notes",
                null,
                new WindowBounds(40, 40, 640, 480),
                null,
                0,
                WindowVisualState.Normal,
                new WindowConstraints(true, 320, 240))));
            HashSet<string> apps = [.. catalog ?? [TaskbarContextMenus.TaskManagerAppId, TaskbarContextMenus.SettingsAppId, "org.hackeros.notes"]];
            Menus = new TaskbarContextMenus(
                ContextMenus,
                new WindowKeyboardCommands(Runtime),
                Runtime,
                _provider.GetRequiredService<IStringLocalizer<Strings>>(),
                apps.Contains,
                _ => canOpenNewWindow,
                (appId, arguments, display) =>
                {
                    Launches.Add((appId, string.Join(' ', arguments), display));
                    return Task.CompletedTask;
                });
        }

        public ContextMenuService ContextMenus { get; } = new();

        public WindowRuntime Runtime { get; }

        public TaskbarContextMenus Menus { get; }

        public List<(string AppId, string Arguments, DisplayId Display)> Launches { get; } = [];

        public async Task<ContextMenuSession> OpenAsync(IReadOnlyDictionary<string, object>? attributes)
        {
            Assert.NotNull(attributes);
            string token = (string)attributes[HackMenuAttributes.Token];
            Assert.True(await ContextMenus.OpenFromTokenAsync(token, 10, 10, ContextMenuOrigin.Pointer));
            return ContextMenus.Current!;
        }

        public void Dispose()
        {
            Menus.Dispose();
            _provider.Dispose();
        }
    }
}
