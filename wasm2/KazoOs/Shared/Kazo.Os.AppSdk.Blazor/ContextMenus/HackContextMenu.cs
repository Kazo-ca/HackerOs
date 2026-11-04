using Kazo.Os.AppSdk.Blazor.Components;
using Kazo.Os.AppSdk.Components;
using Kazo.Os.AppSdk.Icons;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Kazo.Os.AppSdk.Blazor.ContextMenus;

/// <summary>
/// Declares a context menu in markup (ADR 0150 §4), drawn by the OS (contract
/// <c>hackeros.context-menu</c>, provided by the platform). It shows nothing where it is written:
/// any element opens it through <c>@attributes="Menus.Attach(Name, item)"</c>.
/// </summary>
/// <example>
/// <code>
/// &lt;HackContextMenu Name="files"&gt;
///     &lt;HackMenuItem Id="preview" Title="@L["Menu.Preview"]" OnClick="item =&gt; PreviewAsync((Row)item!)" /&gt;
///     &lt;HackMenuGroup&gt;
///         &lt;HackMenuItem Id="export" Title="@L["Menu.Export"]"&gt;
///             &lt;HackMenuItem Id="csv" Title="CSV" OnClick="item =&gt; ExportAsync((Row)item!, "csv")" /&gt;
///         &lt;/HackMenuItem&gt;
///     &lt;/HackMenuGroup&gt;
/// &lt;/HackContextMenu&gt;
/// </code>
/// </example>
public sealed class HackContextMenu : HackContractComponent<IHackContextMenu>
{
    private static readonly RenderFragment<string> NothingWhenUnavailable = _ => _ => { };
    private readonly HackMenuNodeList _nodes = new(depth: 1, isGroup: false);
    private readonly Func<HackMenuDefinition> _build;

    /// <summary>Initializes the component.</summary>
    public HackContextMenu()
    {
        _build = BuildDefinition;
    }

    /// <summary>Gets or sets the menu's name, unique in its window.</summary>
    [Parameter, EditorRequired]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the menu's accessible name; the OS uses a generic one otherwise.</summary>
    [Parameter]
    public string? Label { get; set; }

    /// <summary>Gets or sets the entries: <see cref="HackMenuItem"/> and <see cref="HackMenuGroup"/>.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>
    /// Gets or sets what runs before the menu is drawn: it receives that opening's copy of the menu
    /// and the attached object, and may hide, disable or add entries, or cancel.
    /// </summary>
    [Parameter]
    public EventCallback<HackMenuOpeningEventArgs> Opening { get; set; }

    /// <inheritdoc />
    protected override string Contract => HackComponentContracts.ContextMenu;

    /// <inheritdoc />
    /// <remarks>Nothing is drawn in place of an absent menu: the OS writes the fault to its log.</remarks>
    protected override RenderFragment<string>? DefaultUnavailable => NothingWhenUnavailable;

    /// <inheritdoc />
    protected override void AddProviderParameters(IDictionary<string, object?> parameters)
    {
        parameters[nameof(Name)] = Name;
        parameters[nameof(Label)] = Label;
        parameters[nameof(Opening)] = Opening;
        // The same delegate on every render: the OS keeps its registration until the name changes.
        parameters["Build"] = _build;
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        // The entries only describe themselves to this component; they draw nothing.
        builder.OpenRegion(0);
        builder.OpenComponent<CascadingValue<HackMenuNodeList>>(0);
        builder.AddComponentParameter(1, nameof(CascadingValue<HackMenuNodeList>.Value), _nodes);
        builder.AddComponentParameter(2, nameof(CascadingValue<HackMenuNodeList>.IsFixed), true);
        builder.AddComponentParameter(3, nameof(CascadingValue<HackMenuNodeList>.ChildContent), ChildContent);
        builder.CloseComponent();
        builder.CloseRegion();

        builder.OpenRegion(1);
        base.BuildRenderTree(builder);
        builder.CloseRegion();
    }

    private HackMenuDefinition BuildDefinition()
    {
        HackMenuDefinition menu = new(string.IsNullOrWhiteSpace(Name) ? "menu" : Name) { Label = Label };
        _nodes.AppendTo(menu.Groups);
        return menu;
    }
}

/// <summary>
/// One entry of a <see cref="HackContextMenu"/>. With child entries it opens a submenu and has no
/// action. Prefer <see cref="IsVisible"/> to an <c>@if</c> around it: an entry added after the first
/// render goes to the end of its level.
/// </summary>
public sealed class HackMenuItem : ComponentBase, IHackMenuNode, IDisposable
{
    private HackMenuNodeList? _children;

    [CascadingParameter]
    private HackMenuNodeList? Parent { get; set; }

    /// <summary>Gets or sets the entry id, unique in the menu.</summary>
    [Parameter, EditorRequired]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the text shown, already translated.</summary>
    [Parameter]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the icon (<see cref="HackIcons"/>).</summary>
    [Parameter]
    public HackIcon? Icon { get; set; }

    /// <summary>Gets or sets whether the entry is shown but cannot be chosen.</summary>
    [Parameter]
    public bool IsDisabled { get; set; }

    /// <summary>Gets or sets whether the entry is shown.</summary>
    [Parameter]
    public bool IsVisible { get; set; } = true;

    /// <summary>Gets or sets a shortcut shown as text.</summary>
    [Parameter]
    public string? Shortcut { get; set; }

    /// <summary>Gets or sets the action; receives the object attached to the element.</summary>
    [Parameter]
    public EventCallback<object?> OnClick { get; set; }

    /// <summary>Gets or sets the submenu's entries.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        if (Parent is null)
        {
            throw new InvalidOperationException($"The menu entry '{Id}' must be inside a {nameof(HackContextMenu)}.");
        }

        Parent.Add(this);
    }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (ChildContent is not null && _children is null)
        {
            if (Parent!.Depth >= HackMenuDefinition.MaxDepth)
            {
                throw new InvalidOperationException(
                    $"The menu entry '{Id}' opens a level beyond the {HackMenuDefinition.MaxDepth} a menu may have.");
            }

            _children = new HackMenuNodeList(Parent.Depth + 1, isGroup: false);
        }
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (_children is null)
        {
            return;
        }

        builder.OpenComponent<CascadingValue<HackMenuNodeList>>(0);
        builder.AddComponentParameter(1, nameof(CascadingValue<HackMenuNodeList>.Value), _children);
        builder.AddComponentParameter(2, nameof(CascadingValue<HackMenuNodeList>.IsFixed), true);
        builder.AddComponentParameter(3, nameof(CascadingValue<HackMenuNodeList>.ChildContent), ChildContent);
        builder.CloseComponent();
    }

    /// <summary>Describes the entry with the current parameters.</summary>
    internal HackMenuItemDefinition ToDefinition()
    {
        HackMenuItemDefinition item = new(Id, Title)
        {
            Icon = Icon,
            IsDisabled = IsDisabled,
            IsVisible = IsVisible,
            Shortcut = Shortcut,
            OnClick = OnClick,
        };
        _children?.AppendTo(item.Children);
        return item;
    }

    /// <inheritdoc />
    public void Dispose() => Parent?.Remove(this);
}

/// <summary>
/// An explicit group of a <see cref="HackContextMenu"/> (ADR 0150 §2): the OS draws a separator
/// between two visible groups, never around an empty or hidden one.
/// </summary>
public sealed class HackMenuGroup : ComponentBase, IHackMenuNode, IDisposable
{
    private HackMenuNodeList? _items;

    [CascadingParameter]
    private HackMenuNodeList? Parent { get; set; }

    /// <summary>Gets or sets the group's entries (<see cref="HackMenuItem"/>).</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        if (Parent is null)
        {
            throw new InvalidOperationException($"A {nameof(HackMenuGroup)} must be inside a {nameof(HackContextMenu)}.");
        }

        if (Parent.IsGroup)
        {
            throw new InvalidOperationException($"A {nameof(HackMenuGroup)} cannot contain another group.");
        }

        _items = new HackMenuNodeList(Parent.Depth, isGroup: true);
        Parent.Add(this);
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<CascadingValue<HackMenuNodeList>>(0);
        builder.AddComponentParameter(1, nameof(CascadingValue<HackMenuNodeList>.Value), _items);
        builder.AddComponentParameter(2, nameof(CascadingValue<HackMenuNodeList>.IsFixed), true);
        builder.AddComponentParameter(3, nameof(CascadingValue<HackMenuNodeList>.ChildContent), ChildContent);
        builder.CloseComponent();
    }

    /// <summary>Describes the group with its entries' current parameters.</summary>
    internal HackMenuGroupDefinition ToDefinition()
    {
        HackMenuGroupDefinition group = new();
        foreach (HackMenuItem item in _items!.Items)
        {
            group.Items.Add(item.ToDefinition());
        }

        return group;
    }

    /// <inheritdoc />
    public void Dispose() => Parent?.Remove(this);
}

/// <summary>An entry or a group registered with its parent level.</summary>
internal interface IHackMenuNode;

/// <summary>
/// The entries and groups of one menu level, in the order their components first rendered; loose
/// entries form the implicit group where they appear (ADR 0150 §2).
/// </summary>
internal sealed class HackMenuNodeList(int depth, bool isGroup)
{
    private readonly List<IHackMenuNode> _nodes = [];

    /// <summary>Gets the level: 1 for the menu, 2 for a submenu, 3 for a sub-submenu.</summary>
    public int Depth { get; } = depth;

    /// <summary>Gets whether this list is an explicit group's.</summary>
    public bool IsGroup { get; } = isGroup;

    /// <summary>Gets the entries of a group's list.</summary>
    public IEnumerable<HackMenuItem> Items => _nodes.OfType<HackMenuItem>();

    public void Add(IHackMenuNode node) => _nodes.Add(node);

    public void Remove(IHackMenuNode node) => _nodes.Remove(node);

    /// <summary>Appends this level's groups to <paramref name="groups"/>.</summary>
    public void AppendTo(IList<HackMenuGroupDefinition> groups)
    {
        HackMenuGroupDefinition? loose = null;
        foreach (IHackMenuNode node in _nodes)
        {
            switch (node)
            {
                case HackMenuItem item:
                    if (loose is null)
                    {
                        loose = new HackMenuGroupDefinition();
                        groups.Add(loose);
                    }

                    loose.Items.Add(item.ToDefinition());
                    break;
                case HackMenuGroup group:
                    groups.Add(group.ToDefinition());
                    loose = null;
                    break;
            }
        }
    }
}
