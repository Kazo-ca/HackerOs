using Kazo.Os.AppSdk.Icons;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.AppSdk.Blazor.ContextMenus;

/// <summary>
/// Builds a context menu in C# (<c>Menus.Define(name, builder =&gt; …)</c>, ADR 0150 §4), with the
/// same model as the markup components: entries outside an explicit <see cref="Group"/> form the
/// implicit group where they appear.
/// </summary>
/// <example>
/// <code>
/// Menus.Define("files", menu =&gt; menu
///     .Item("preview", L["Menu.Preview"], item =&gt; PreviewAsync((Row)item!))
///     .Group(group =&gt; group
///         .Submenu("export", L["Menu.Export"], export =&gt; export
///             .Item("csv", "CSV", item =&gt; ExportAsync((Row)item!, "csv")))));
/// </code>
/// </example>
public sealed class HackMenuBuilder
{
    private readonly IList<HackMenuGroupDefinition> _groups;
    private readonly object? _receiver;
    private readonly int _depth;
    private HackMenuGroupDefinition? _implicit;

    /// <summary>Initializes a builder filling <paramref name="menu"/>.</summary>
    /// <param name="menu">The menu to fill.</param>
    /// <param name="receiver">
    /// The component that owns the callbacks: it re-renders after an action, and the action runs in
    /// its app's context, as an <c>@onclick</c> handler would. <see langword="null"/> runs the plain delegate.
    /// </param>
    public HackMenuBuilder(HackMenuDefinition menu, object? receiver)
        : this((menu ?? throw new ArgumentNullException(nameof(menu))).Groups, receiver, depth: 1)
    {
    }

    private HackMenuBuilder(IList<HackMenuGroupDefinition> groups, object? receiver, int depth)
    {
        _groups = groups;
        _receiver = receiver;
        _depth = depth;
    }

    /// <summary>Adds an entry that runs <paramref name="onClick"/> with the attached object.</summary>
    /// <param name="id">The entry id, unique in the menu.</param>
    /// <param name="title">The text shown, already translated.</param>
    /// <param name="onClick">The action; receives the object attached to the element.</param>
    /// <param name="icon">The icon, or <see langword="null"/>.</param>
    /// <param name="shortcut">A shortcut shown as text, or <see langword="null"/>.</param>
    /// <param name="isDisabled">Whether the entry is shown but cannot be chosen.</param>
    /// <returns>This builder.</returns>
    public HackMenuBuilder Item(
        string id,
        string title,
        Func<object?, Task> onClick,
        HackIcon? icon = null,
        string? shortcut = null,
        bool isDisabled = false)
    {
        ArgumentNullException.ThrowIfNull(onClick);
        HackMenuItemDefinition item = CurrentGroup().Add(id, title);
        item.Icon = icon;
        item.Shortcut = shortcut;
        item.IsDisabled = isDisabled;
        item.OnClick = _receiver is null
            ? new EventCallback<object?>(null, onClick)
            : EventCallback.Factory.Create<object?>(_receiver, onClick);
        return this;
    }

    /// <summary>Adds an entry that opens a submenu.</summary>
    /// <param name="id">The entry id, unique in the menu.</param>
    /// <param name="title">The text shown, already translated.</param>
    /// <param name="children">Fills the submenu.</param>
    /// <param name="icon">The icon, or <see langword="null"/>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The submenu would be deeper than <see cref="HackMenuDefinition.MaxDepth"/>.</exception>
    public HackMenuBuilder Submenu(string id, string title, Action<HackMenuBuilder> children, HackIcon? icon = null)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (_depth >= HackMenuDefinition.MaxDepth)
        {
            throw new InvalidOperationException(
                $"The entry '{id}' opens a level beyond the {HackMenuDefinition.MaxDepth} a menu may have.");
        }

        HackMenuItemDefinition item = CurrentGroup().Add(id, title);
        item.Icon = icon;
        children(new HackMenuBuilder(item.Children, _receiver, _depth + 1));
        return this;
    }

    /// <summary>Adds an explicit group: separators are drawn around it when its neighbours are visible.</summary>
    /// <param name="group">Fills the group.</param>
    /// <returns>This builder.</returns>
    public HackMenuBuilder Group(Action<HackMenuBuilder> group)
    {
        ArgumentNullException.ThrowIfNull(group);
        HackMenuGroupDefinition definition = new();
        _groups.Add(definition);
        _implicit = null;
        // The group's own builder adds every loose entry to that one group.
        HackMenuBuilder inner = new(_groups, _receiver, _depth) { _implicit = definition };
        group(inner);
        return this;
    }

    private HackMenuGroupDefinition CurrentGroup()
    {
        if (_implicit is null)
        {
            _implicit = new HackMenuGroupDefinition();
            _groups.Add(_implicit);
        }

        return _implicit;
    }
}
