using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Blazor.ContextMenus;
using Kazo.Os.AppSdk.Blazor.Files;
using Kazo.Os.AppSdk.Files;
using Kazo.Os.AppSdk.Icons;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.Apps.FileExplorer;

/// <summary>What the file menu is drawn for (ADR 0155 §4).</summary>
/// <param name="Targets">The items it acts on; empty for the folder's background.</param>
/// <param name="Features">What the host lets the control do (<see cref="KazoFileViewFeatures"/>).</param>
/// <param name="CanPaste">Whether the session clipboard holds files.</param>
/// <param name="Handlers">The apps that declare the one targeted file, default first; empty otherwise.</param>
internal sealed record ExplorerFileMenuState(
    IReadOnlyList<KazoFileViewItem> Targets,
    KazoFileViewFeatures Features,
    bool CanPaste,
    IReadOnlyList<KazoAppInfo> Handlers);

/// <summary>
/// Builds the default file menus of plan §7 on the OS menu model, with no state of its own: the
/// control gives what was right-clicked and how each entry runs, and the host's
/// <c>ContextMenuOpening</c> then adds, hides or disables entries by their <see cref="KazoFileMenuItems"/> ids.
/// </summary>
/// <remarks>
/// <para>An item: [Open] [Open with ▸] [Cut, Copy, Paste into folder, Rename, Delete, Delete permanently]
/// [the apps' entries, chantier 4] [Properties].</para>
/// <para>The background: [New folder, Paste, Refresh] [Properties of the folder].</para>
/// <para>Several items: Rename and "Open with" are left out, Properties is disabled, as in Nautilus.
/// An entry the host's <see cref="KazoFileViewFeatures"/> forbids is left out; the OS collapses the
/// separators around empty groups.</para>
/// </remarks>
internal static class ExplorerFileMenu
{
    /// <summary>The scope of a menu over <paramref name="targets"/>: a folder only when every target is one.</summary>
    internal static FileMenuScope ScopeOf(IReadOnlyList<KazoFileViewItem> targets) =>
        targets.Count == 0 ? FileMenuScope.Background
        : targets.All(target => target.IsDirectory) ? FileMenuScope.Directory
        : FileMenuScope.File;

    /// <summary>Replaces the content of <paramref name="menu"/> with the default menu for <paramref name="state"/>.</summary>
    /// <param name="menu">The copy of the menu the OS is about to draw.</param>
    /// <param name="state">What was right-clicked and what the control may do.</param>
    /// <param name="command">The click callback of an entry id (<c>open-with:&lt;appId&gt;</c> for an app).</param>
    /// <param name="text">The explorer's texts in the host's language.</param>
    internal static void Build(
        KazoMenuDefinition menu,
        ExplorerFileMenuState state,
        Func<string, EventCallback<object?>> command,
        Func<string, string> text)
    {
        menu.Groups.Clear();
        if (state.Targets.Count == 0)
        {
            BuildBackground(menu, state, command, text);
        }
        else
        {
            BuildItems(menu, state, command, text);
        }
    }

    private static void BuildItems(
        KazoMenuDefinition menu,
        ExplorerFileMenuState state,
        Func<string, EventCallback<object?>> command,
        Func<string, string> text)
    {
        IReadOnlyList<KazoFileViewItem> targets = state.Targets;
        bool single = targets.Count == 1;
        KazoFileViewItem first = targets[0];

        // Open: several folders cannot all be entered in one view, several files each open.
        Entry(menu.AddGroup(), KazoFileMenuItems.Open, text("Menu.Open"), "folder-open", command,
            isDisabled: !single && targets.All(target => target.IsDirectory));

        if (single && !first.IsDirectory && Has(state, KazoFileViewFeatures.OpenWith))
        {
            KazoMenuItemDefinition openWith = menu.AddGroup().Add(KazoFileMenuItems.OpenWith, text("Menu.OpenWith"));
            openWith.Icon = Icon("app-window");
            if (state.Handlers.Count > 0)
            {
                KazoMenuGroupDefinition apps = new();
                foreach (KazoAppInfo handler in state.Handlers)
                {
                    Entry(apps, KazoFileMenuItems.OpenWithAppPrefix + handler.Id, handler.Name, icon: null, command);
                }

                openWith.Children.Add(apps);
            }

            KazoMenuGroupDefinition other = new();
            Entry(other, KazoFileMenuItems.OpenWithOther, text("Menu.OpenWithOther"), icon: null, command);
            openWith.Children.Add(other);
        }

        KazoMenuGroupDefinition edit = menu.AddGroup();
        if (Has(state, KazoFileViewFeatures.Clipboard))
        {
            Entry(edit, KazoFileMenuItems.Cut, text("Menu.Cut"), "scissors", command, shortcut: "Ctrl+X");
            Entry(edit, KazoFileMenuItems.Copy, text("Menu.Copy"), "copy", command, shortcut: "Ctrl+C");
            if (single && first.IsDirectory)
            {
                Entry(edit, KazoFileMenuItems.PasteInto, text("Menu.PasteInto"), "clipboard-paste", command, isDisabled: !state.CanPaste);
            }
        }

        if (single && Has(state, KazoFileViewFeatures.Rename))
        {
            Entry(edit, KazoFileMenuItems.Rename, text("Menu.Rename"), "pencil", command, shortcut: "F2");
        }

        if (Has(state, KazoFileViewFeatures.Delete))
        {
            Entry(edit, KazoFileMenuItems.Delete, text("Menu.Delete"), "trash-2", command, shortcut: "Del");
            Entry(edit, KazoFileMenuItems.DeletePermanently, text("Menu.DeletePermanently"), "octagon-x", command, shortcut: "Shift+Del");
        }

        // The apps' registered entries land in this group (chantier 4, ADR 0151); a host adds its own here.
        menu.AddGroup();

        if (Has(state, KazoFileViewFeatures.Properties))
        {
            Entry(menu.AddGroup(), KazoFileMenuItems.Properties, text("Menu.Properties"), "info", command, isDisabled: !single);
        }
    }

    private static void BuildBackground(
        KazoMenuDefinition menu,
        ExplorerFileMenuState state,
        Func<string, EventCallback<object?>> command,
        Func<string, string> text)
    {
        KazoMenuGroupDefinition main = menu.AddGroup();
        if (Has(state, KazoFileViewFeatures.CreateFolder))
        {
            Entry(main, KazoFileMenuItems.NewFolder, text("Menu.NewFolder"), "folder-plus", command);
        }

        if (Has(state, KazoFileViewFeatures.Clipboard))
        {
            Entry(main, KazoFileMenuItems.Paste, text("Menu.Paste"), "clipboard-paste", command, shortcut: "Ctrl+V", isDisabled: !state.CanPaste);
        }

        Entry(main, KazoFileMenuItems.Refresh, text("Menu.Refresh"), "refresh-cw", command, shortcut: "F5");

        if (Has(state, KazoFileViewFeatures.Properties))
        {
            Entry(menu.AddGroup(), KazoFileMenuItems.Properties, text("Menu.Properties"), "info", command);
        }
    }

    private static void Entry(
        KazoMenuGroupDefinition group,
        string id,
        string title,
        string? icon,
        Func<string, EventCallback<object?>> command,
        string? shortcut = null,
        bool isDisabled = false)
    {
        KazoMenuItemDefinition entry = group.Add(id, title);
        entry.Icon = icon is null ? null : Icon(icon);
        entry.Shortcut = shortcut;
        entry.IsDisabled = isDisabled;
        entry.OnClick = command(id);
    }

    private static KazoSurfaceIcon Icon(string name) => KazoIcons.Vector(IconLibrary.Lucide, name);

    private static bool Has(ExplorerFileMenuState state, KazoFileViewFeatures feature) => (state.Features & feature) == feature;
}
