using Kazo.Os.AppSdk.Blazor.ContextMenus;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.AppSdk.Blazor;

/// <summary>
/// Context menus of a window app (ADR 0150): the OS draws them, the app declares them and attaches
/// them to its elements.
/// </summary>
public abstract partial class WindowAppBase
{
    private KazoMenus? _menus;

    /// <summary>Gets or sets the window's context menus, cascaded by the OS (ADR 0150 §4).</summary>
    [CascadingParameter]
    private IKazoMenuHost? MenuHost { get; set; }

    /// <summary>
    /// Gets the window's context menus (ADR 0150 §4): <c>@attributes="Menus.Attach(name, item)"</c>
    /// on any element, <c>Menus.Define(name, builder =&gt; …)</c> in C#, and <c>Menus.OpenAsync</c>
    /// for a menu button. Callbacks defined here run in the app's context and re-render the window.
    /// </summary>
    protected KazoMenus Menus => _menus is { } menus && menus.Host == MenuHost
        ? menus
        : _menus = new KazoMenus(MenuHost, this);
}
