using HackerOs.AppSdk.Blazor.ContextMenus;
using HackerOs.Platform.Blazor.ContextMenus;
using HackerOs.Platform.Blazor.Windows;
using HackerOs.Taskbar.Blazor;
using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Blazor;
using HackerOs.Windowing.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace HackerOs.Platform.Blazor.Shell;

/// <summary>
/// The taskbar's context menus (ADR 0194): the window menu on a window's button, and the taskbar
/// menu on the rest of the bar. Both open in the OS context menu (ADR 0150), so a right click, a
/// long press, the Menu key and Shift+F10 open them on every screen's taskbar.
/// </summary>
internal sealed class TaskbarContextMenus : IDisposable
{
    /// <summary>Name of the menu of a window's taskbar button.</summary>
    internal const string WindowMenuName = "taskbar-window-menu";

    /// <summary>Name of the menu of the taskbar's empty area.</summary>
    internal const string TaskbarMenuName = "taskbar-menu";

    /// <summary>Id of « Nouvelle fenêtre ».</summary>
    internal const string NewWindowItemId = "taskbar.new-window";

    /// <summary>Id of « Gestionnaire des tâches ».</summary>
    internal const string TaskManagerItemId = "taskbar.task-manager";

    /// <summary>Id of « Afficher le bureau ».</summary>
    internal const string ShowDesktopItemId = "taskbar.show-desktop";

    /// <summary>Id of « Fenêtres en cascade ».</summary>
    internal const string CascadeItemId = "taskbar.cascade";

    /// <summary>Id of « Fenêtres en mosaïque ».</summary>
    internal const string TileItemId = "taskbar.tile";

    /// <summary>Id of « Paramètres de la barre des tâches ».</summary>
    internal const string SettingsItemId = "taskbar.settings";

    /// <summary>The app « Gestionnaire des tâches » opens.</summary>
    internal const string TaskManagerAppId = "org.hackeros.system-monitor";

    /// <summary>The app « Paramètres de la barre des tâches » opens.</summary>
    internal const string SettingsAppId = "org.hackeros.settings";

    /// <summary>The Settings page that holds the taskbar's setting (<c>primary-taskbar</c>, ADR 0179 §7).</summary>
    internal const string SettingsPageArgument = "--page=screens";

    private readonly ContextMenuScope _scope;
    private readonly WindowKeyboardCommands _commands;
    private readonly WindowRuntime _runtime;
    private readonly IStringLocalizer _text;
    private readonly Func<string, bool> _canLaunch;
    private readonly Func<string, bool> _canOpenNewWindow;
    private readonly Func<string, IReadOnlyList<string>, DisplayId, Task> _launch;
    private readonly List<IDisposable> _registrations = [];
    private readonly Dictionary<DisplayId, ScreenMenus> _screens = [];

    /// <summary>Registers the two menus in a scope of the OS context menu.</summary>
    /// <param name="contextMenus">The OS context menu.</param>
    /// <param name="commands">The window manager's commands, which run the window menu.</param>
    /// <param name="runtime">The window runtime.</param>
    /// <param name="text">The platform's texts.</param>
    /// <param name="canLaunch">Whether an app id is in the catalog.</param>
    /// <param name="canOpenNewWindow">Whether the app of a window accepts one more window.</param>
    /// <param name="launch">Starts an app with its arguments on a screen.</param>
    public TaskbarContextMenus(
        ContextMenuService contextMenus,
        WindowKeyboardCommands commands,
        WindowRuntime runtime,
        IStringLocalizer text,
        Func<string, bool> canLaunch,
        Func<string, bool> canOpenNewWindow,
        Func<string, IReadOnlyList<string>, DisplayId, Task> launch)
    {
        ArgumentNullException.ThrowIfNull(contextMenus);
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _canLaunch = canLaunch ?? throw new ArgumentNullException(nameof(canLaunch));
        _canOpenNewWindow = canOpenNewWindow ?? throw new ArgumentNullException(nameof(canOpenNewWindow));
        _launch = launch ?? throw new ArgumentNullException(nameof(launch));
        _scope = contextMenus.CreateScope(null);
        _registrations.Add(_scope.Register(new HackMenuRegistration(
            WindowMenuName,
            () => new HackMenuDefinition(WindowMenuName) { Label = _text["WindowMenu.Label"] },
            EventCallback.Factory.Create<HackMenuOpeningEventArgs>(this, FillWindowMenu))));
        _registrations.Add(_scope.Register(new HackMenuRegistration(
            TaskbarMenuName,
            () => new HackMenuDefinition(TaskbarMenuName) { Label = _text["TaskbarMenu.Label"] },
            EventCallback.Factory.Create<HackMenuOpeningEventArgs>(this, FillTaskbarMenu))));
    }

    /// <summary>Gets the menus of the taskbar of one screen.</summary>
    /// <param name="display">The screen of the taskbar.</param>
    public ITaskbarContextMenus For(DisplayId display)
    {
        if (!_screens.TryGetValue(display, out ScreenMenus? menus))
        {
            menus = new ScreenMenus(_scope, display);
            _screens[display] = menus;
        }

        return menus;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (IDisposable registration in _registrations)
        {
            registration.Dispose();
        }

        _registrations.Clear();
        _scope.Dispose();
    }

    /// <summary>« Nouvelle fenêtre », then the window menu and « Autre écran ▸ » (ADR 0194 §3).</summary>
    private void FillWindowMenu(HackMenuOpeningEventArgs args)
    {
        if (args.Item is not TaskbarWindowTarget target
            || _runtime.Windows.FirstOrDefault(window => window.Id == target.Window) is not { } window)
        {
            args.Cancel = true;
            return;
        }

        string appId = window.AppId;
        DisplayId display = target.Display;
        HackMenuItemDefinition newWindow = args.Menu.AddGroup().Add(NewWindowItemId, _text["TaskbarMenu.NewWindow"]);
        newWindow.IsDisabled = !_canOpenNewWindow(appId);
        newWindow.OnClick = EventCallback.Factory.Create<object?>(this, () => _launch(appId, [], display));
        WindowMenuDefinitions.AddWindowEntries(args.Menu, _commands, _text, window.Id, WindowMenuKind.Window, this);
    }

    /// <summary>The taskbar menu of one screen (ADR 0194 §4).</summary>
    private void FillTaskbarMenu(HackMenuOpeningEventArgs args)
    {
        if (args.Item is not DisplayId display)
        {
            args.Cancel = true;
            return;
        }

        bool hasWindows = WindowArrangement.ArrangeableWindows(_runtime, display).Count > 0;
        HackMenuItemDefinition taskManager = args.Menu.AddGroup().Add(TaskManagerItemId, _text["TaskbarMenu.TaskManager"]);
        taskManager.IsDisabled = !_canLaunch(TaskManagerAppId);
        taskManager.OnClick = EventCallback.Factory.Create<object?>(this, () => _launch(TaskManagerAppId, [], display));

        HackMenuGroupDefinition windows = args.Menu.AddGroup();
        AddArrangement(windows, ShowDesktopItemId, "TaskbarMenu.ShowDesktop", hasWindows, () => WindowArrangement.ShowDesktop(_runtime, display));
        AddArrangement(windows, CascadeItemId, "TaskbarMenu.Cascade", hasWindows, () => WindowArrangement.Cascade(_runtime, display));
        AddArrangement(windows, TileItemId, "TaskbarMenu.Tile", hasWindows, () => WindowArrangement.Tile(_runtime, display));

        HackMenuItemDefinition settings = args.Menu.AddGroup().Add(SettingsItemId, _text["TaskbarMenu.Settings"]);
        settings.IsDisabled = !_canLaunch(SettingsAppId);
        settings.OnClick = EventCallback.Factory.Create<object?>(this, () => _launch(SettingsAppId, [SettingsPageArgument], display));
    }

    private void AddArrangement(HackMenuGroupDefinition group, string id, string textKey, bool isEnabled, Func<int> arrange)
    {
        HackMenuItemDefinition entry = group.Add(id, _text[textKey]);
        entry.IsDisabled = !isEnabled;
        entry.OnClick = EventCallback.Factory.Create<object?>(this, () => arrange());
    }

    /// <summary>The window of a taskbar button, with the screen of that taskbar.</summary>
    /// <param name="Window">The window.</param>
    /// <param name="Display">The screen of the taskbar the button is on.</param>
    internal readonly record struct TaskbarWindowTarget(WindowId Window, DisplayId Display);

    /// <summary>The attributes of one screen's taskbar.</summary>
    private sealed class ScreenMenus(ContextMenuScope scope, DisplayId display) : ITaskbarContextMenus
    {
        public IReadOnlyDictionary<string, object>? TaskbarAttributes =>
            scope.IsDisposed ? null : scope.Attach(TaskbarMenuName, display);

        public IReadOnlyDictionary<string, object>? WindowItemAttributes(WindowId id) =>
            scope.IsDisposed ? null : scope.Attach(WindowMenuName, new TaskbarWindowTarget(id, display));
    }
}
