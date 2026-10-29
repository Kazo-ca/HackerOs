using HackerOs.AppSdk.Blazor.ContextMenus;
using HackerOs.Platform.Core.Shell;
using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace HackerOs.Platform.Blazor.Windows;

/// <summary>
/// Builds the entries of a window menu (ADR 0179 §3-5): its commands, then « Autre écran ▸ ». The
/// title bar's menu and the taskbar button's menu (ADR 0194 §3) share them.
/// </summary>
internal static class WindowMenuDefinitions
{
    /// <summary>Adds the window's commands and the « Autre écran » submenu to <paramref name="definition"/>.</summary>
    /// <param name="definition">The menu to fill.</param>
    /// <param name="commands">The window manager's commands, which run the entries.</param>
    /// <param name="text">The platform's texts.</param>
    /// <param name="windowId">The window the menu acts on.</param>
    /// <param name="kind">The window menu, or the Maximize button's.</param>
    /// <param name="receiver">The object the entries' callbacks are created on.</param>
    public static void AddWindowEntries(
        HackMenuDefinition definition,
        WindowKeyboardCommands commands,
        IStringLocalizer text,
        WindowId windowId,
        WindowMenuKind kind,
        object receiver)
    {
        HackMenuGroupDefinition group = definition.AddGroup();
        foreach (WindowMenuItem item in commands.GetMenuItems(windowId, kind))
        {
            WindowKeyboardAction action = item.Action;
            HackMenuItemDefinition entry = group.Add("window." + WindowKeyBindingsSettingsDocuments.KeyOf(action), item.Label);
            entry.Shortcut = item.Shortcut;
            entry.IsDisabled = !item.IsEnabled;
            entry.OnClick = EventCallback.Factory.Create<object?>(receiver, () => commands.ExecuteForWindowAsync(action, windowId));
        }

        HackMenuItemDefinition otherScreen = definition.AddGroup().Add(WindowManagerOverlays.OtherScreenItemId, text["WindowMenu.OtherScreen"]);
        HackMenuGroupDefinition screens = new();
        foreach (WindowScreenMenuItem screen in commands.GetScreenMenuItems(windowId))
        {
            DisplayId display = screen.Display;
            HackMenuItemDefinition entry = screens.Add(
                WindowManagerOverlays.ScreenItemIdPrefix + display.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                screen.Label);
            entry.IsDisabled = !screen.IsEnabled;
            entry.OnClick = EventCallback.Factory.Create<object?>(receiver, () => commands.MoveToScreen(windowId, display));
        }

        otherScreen.Children.Add(screens);
        HackMenuGroupDefinition create = new();
        HackMenuItemDefinition newScreen = create.Add(WindowManagerOverlays.NewScreenItemId, text["WindowMenu.NewScreen"]);
        newScreen.IsDisabled = !commands.CanOpenScreen;
        newScreen.OnClick = EventCallback.Factory.Create<object?>(receiver, () => commands.MoveToNewScreenAsync(windowId));
        otherScreen.Children.Add(create);
    }
}
