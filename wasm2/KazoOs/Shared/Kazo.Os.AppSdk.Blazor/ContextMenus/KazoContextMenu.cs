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
/// &lt;KazoContextMenu Name="files"&gt;
///     &lt;KazoMenuItem Id="preview" Title="@L["Menu.Preview"]" OnClick="item =&gt; PreviewAsync((Row)item!)" /&gt;
///     &lt;KazoMenuGroup&gt;
///         &lt;KazoMenuItem Id="export" Title="@L["Menu.Export"]"&gt;
///             &lt;KazoMenuItem Id="csv" Title="CSV" OnClick="item =&gt; ExportAsync((Row)item!, "csv")" /&gt;
///         &lt;/KazoMenuItem&gt;
///     &lt;/KazoMenuGroup&gt;
/// &lt;/KazoContextMenu&gt;
/// </code>
/// </example>
public sealed class KazoContextMenu : KazoContractComponent<IHackContextMenu>
{
    private static readonly RenderFragment<string> NothingWhenUnavailable = _ => _ => { };
    private readonly KazoMenuNodeList _nodes = new(depth: 1, isGroup: false);
    private readonly Func<KazoMenuDefinition> _build;

    /// <summary>Initializes the component.</summary>
    public KazoContextMenu()
    {
        _build = BuildDefinition;
    }

    /// <summary>Gets or sets the menu's name, unique in its window.</summary>
    [Parameter, EditorRequired]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the menu's accessible name; the OS uses a generic one otherwise.</summary>
    [Parameter]
    public string? Label { get; set; }

    /// <summary>Gets or sets the entries: <see cref="KazoMenuItem"/> and <see cref="KazoMenuGroup"/>.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>
    /// Gets or sets what runs before the menu is drawn: it receives that opening's copy of the menu
    /// and the attached object, and may hide, disable or add entries, or cancel.
    /// </summary>
    [Parameter]
    public EventCallback<KazoMenuOpeningEventArgs> Opening { get; set; }

    /// <inheritdoc />
    protected override string Contract => KazoComponentContracts.ContextMenu;

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
        builder.OpenComponent<CascadingValue<KazoMenuNodeList>>(0);
        builder.AddComponentParameter(1, nameof(CascadingValue<KazoMenuNodeList>.Value), _nodes);
        builder.AddComponentParameter(2, nameof(CascadingValue<KazoMenuNodeList>.IsFixed), true);
        builder.AddComponentParameter(3, nameof(CascadingValue<KazoMenuNodeList>.ChildContent), ChildContent);
        builder.CloseComponent();
        builder.CloseRegion();

        builder.OpenRegion(1);
        base.BuildRenderTree(builder);
        builder.CloseRegion();
    }

    private KazoMenuDefinition BuildDefinition()
    {
        KazoMenuDefinition menu = new(string.IsNullOrWhiteSpace(Name) ? "menu" : Name) { Label = Label };
        _nodes.AppendTo(menu.Groups);
        return menu;
    }
}

/// <summary>
/// One entry of a <see cref="KazoContextMenu"/>. With child entries it opens a submenu and has no
/// action. Prefer <see cref="IsVisible"/> to an <c>@if</c> around it: an entry added after the first
/// render goes to the end of its level.
/// </summary>
public sealed class KazoMenuItem : ComponentBase, IHackMenuNode, IDisposable
{
    private KazoMenuNodeList? _children;

    [CascadingParameter]
    private KazoMenuNodeList? Parent { get; set; }

    /// <summary>Gets or sets the entry id, unique in the menu.</summary>
    [Parameter, EditorRequired]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the text shown, already translated.</summary>
    [Parameter]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the icon (<see cref="KazoIcons"/>).</summary>
    [Parameter]
    public KazoSurfaceIcon? Icon { get; set; }

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
            throw new InvalidOperationException($"The menu entry '{Id}' must be inside a {nameof(KazoContextMenu)}.");
        }

        Parent.Add(this);
    }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (ChildContent is not null && _children is null)
        {
            if (Parent!.Depth >= KazoMenuDefinition.MaxDepth)
            {
                throw new InvalidOperationException(
                    $"The menu entry '{Id}' opens a level beyond the {KazoMenuDefinition.MaxDepth} a menu may have.");
            }

            _children = new KazoMenuNodeList(Parent.Depth + 1, isGroup: false);
        }
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (_children is null)
        {
            return;
        }

        builder.OpenComponent<CascadingValue<KazoMenuNodeList>>(0);
        builder.AddComponentParameter(1, nameof(CascadingValue<KazoMenuNodeList>.Value), _children);
        builder.AddComponentParameter(2, nameof(CascadingValue<KazoMenuNodeList>.IsFixed), true);
        builder.AddComponentParameter(3, nameof(CascadingValue<KazoMenuNodeList>.ChildContent), ChildContent);
        builder.CloseComponent();
    }

    /// <summary>Describes the entry with the current parameters.</summary>
    internal KazoMenuItemDefinition ToDefinition()
    {
        KazoMenuItemDefinition item = new(Id, Title)
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
/// An explicit group of a <see cref="KazoContextMenu"/> (ADR 0150 §2): the OS draws a separator
/// between two visible groups, never around an empty or hidden one.
/// </summary>
public sealed class KazoMenuGroup : ComponentBase, IHackMenuNode, IDisposable
{
    private KazoMenuNodeList? _items;

    [CascadingParameter]
    private KazoMenuNodeList? Parent { get; set; }

    /// <summary>Gets or sets the group's entries (<see cref="KazoMenuItem"/>).</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        if (Parent is null)
        {
            throw new InvalidOperationException($"A {nameof(KazoMenuGroup)} must be inside a {nameof(KazoContextMenu)}.");
        }

        if (Parent.IsGroup)
        {
            throw new InvalidOperationException($"A {nameof(KazoMenuGroup)} cannot contain another group.");
        }

        _items = new KazoMenuNodeList(Parent.Depth, isGroup: true);
        Parent.Add(this);
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<CascadingValue<KazoMenuNodeList>>(0);
        builder.AddComponentParameter(1, nameof(CascadingValue<KazoMenuNodeList>.Value), _items);
        builder.AddComponentParameter(2, nameof(CascadingValue<KazoMenuNodeList>.IsFixed), true);
        builder.AddComponentParameter(3, nameof(CascadingValue<KazoMenuNodeList>.ChildContent), ChildContent);
        builder.CloseComponent();
    }

    /// <summary>Describes the group with its entries' current parameters.</summary>
    internal KazoMenuGroupDefinition ToDefinition()
    {
        KazoMenuGroupDefinition group = new();
        foreach (KazoMenuItem item in _items!.Items)
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
internal sealed class KazoMenuNodeList(int depth, bool isGroup)
{
    private readonly List<IHackMenuNode> _nodes = [];

    /// <summary>Gets the level: 1 for the menu, 2 for a submenu, 3 for a sub-submenu.</summary>
    public int Depth { get; } = depth;

    /// <summary>Gets whether this list is an explicit group's.</summary>
    public bool IsGroup { get; } = isGroup;

    /// <summary>Gets the entries of a group's list.</summary>
    public IEnumerable<KazoMenuItem> Items => _nodes.OfType<KazoMenuItem>();

    public void Add(IHackMenuNode node) => _nodes.Add(node);

    public void Remove(IHackMenuNode node) => _nodes.Remove(node);

    /// <summary>Appends this level's groups to <paramref name="groups"/>.</summary>
    public void AppendTo(IList<KazoMenuGroupDefinition> groups)
    {
        KazoMenuGroupDefinition? loose = null;
        foreach (IHackMenuNode node in _nodes)
        {
            switch (node)
            {
                case KazoMenuItem item:
                    if (loose is null)
                    {
                        loose = new KazoMenuGroupDefinition();
                        groups.Add(loose);
                    }

                    loose.Items.Add(item.ToDefinition());
                    break;
                case KazoMenuGroup group:
                    groups.Add(group.ToDefinition());
                    loose = null;
                    break;
            }
        }
    }
}
