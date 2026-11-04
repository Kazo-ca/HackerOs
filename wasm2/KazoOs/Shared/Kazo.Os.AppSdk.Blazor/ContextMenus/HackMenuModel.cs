using Kazo.Os.AppSdk.Blazor.Files;
using Kazo.Os.AppSdk.Icons;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.AppSdk.Blazor.ContextMenus;

/// <summary>
/// A context menu as an app defines it (ADR 0150 §2): ordered groups of entries. The OS draws a
/// separator between two visible groups only, so an app never writes a separator itself.
/// </summary>
/// <remarks>
/// The OS builds a fresh copy each time the menu opens and hands it to the menu's
/// <c>Opening</c> handler, which may hide, disable or add entries for the attached object before
/// anything is drawn.
/// </remarks>
public sealed class HackMenuDefinition
{
    /// <summary>The deepest menu level: the menu, a submenu and a sub-submenu (ADR 0150 §2).</summary>
    public const int MaxDepth = 3;

    /// <summary>Initializes a menu.</summary>
    /// <param name="name">The menu's name in its window, as <c>Menus.Attach</c> references it.</param>
    public HackMenuDefinition(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Gets the menu's name.</summary>
    public string Name { get; }

    /// <summary>Gets an optional accessible name for the menu; the OS uses a generic one otherwise.</summary>
    public string? Label { get; set; }

    /// <summary>Gets the menu's groups, in order.</summary>
    public IList<HackMenuGroupDefinition> Groups { get; } = [];

    /// <summary>
    /// Gets or sets what a file menu is drawn for, or <see langword="null"/> for any other menu
    /// (ADR 0159 §5). The provider of the file control (<c>hackeros.file-view</c>) sets it in its
    /// <c>Opening</c> handler; the OS then adds the entries apps registered (ADR 0151) to this
    /// opening, whichever app provides the control.
    /// </summary>
    public HackFileMenuContext? FileMenu { get; set; }

    /// <summary>Finds an entry by id, at any depth.</summary>
    /// <param name="id">The entry id.</param>
    /// <returns>The entry, or <see langword="null"/>.</returns>
    public HackMenuItemDefinition? Find(string id) => Find(Groups, id);

    /// <summary>Hides an entry for this opening; does nothing when the id is not in the menu.</summary>
    /// <param name="id">The entry id.</param>
    public void Hide(string id)
    {
        if (Find(id) is { } item)
        {
            item.IsVisible = false;
        }
    }

    /// <summary>Disables an entry for this opening; does nothing when the id is not in the menu.</summary>
    /// <param name="id">The entry id.</param>
    public void Disable(string id)
    {
        if (Find(id) is { } item)
        {
            item.IsDisabled = true;
        }
    }

    /// <summary>Adds a group at the end of the menu and returns it.</summary>
    public HackMenuGroupDefinition AddGroup()
    {
        HackMenuGroupDefinition group = new();
        Groups.Add(group);
        return group;
    }

    /// <summary>Copies the menu, entries and groups included; callbacks and icons are shared.</summary>
    public HackMenuDefinition Clone()
    {
        HackMenuDefinition copy = new(Name) { Label = Label, FileMenu = FileMenu };
        foreach (HackMenuGroupDefinition group in Groups)
        {
            copy.Groups.Add(group.Clone());
        }

        return copy;
    }

    /// <summary>
    /// Checks the rules the OS relies on: ids present and unique in the whole menu, at most
    /// <see cref="MaxDepth"/> levels, and no action on an entry that opens a submenu.
    /// </summary>
    /// <exception cref="InvalidOperationException">The menu breaks a rule; the message names the entry.</exception>
    public void Validate()
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        Validate(Groups, depth: 1, ids);
    }

    private void Validate(IEnumerable<HackMenuGroupDefinition> groups, int depth, HashSet<string> ids)
    {
        foreach (HackMenuItemDefinition item in groups.SelectMany(group => group.Items))
        {
            if (string.IsNullOrWhiteSpace(item.Id))
            {
                throw new InvalidOperationException($"An entry of the menu '{Name}' has no id.");
            }

            if (!ids.Add(item.Id))
            {
                throw new InvalidOperationException($"The menu '{Name}' has two entries with the id '{item.Id}'.");
            }

            if (item.Children.Count == 0)
            {
                continue;
            }

            if (depth >= MaxDepth)
            {
                throw new InvalidOperationException(
                    $"The entry '{item.Id}' of the menu '{Name}' opens a level beyond the {MaxDepth} a menu may have.");
            }

            if (item.OnClick.HasDelegate)
            {
                throw new InvalidOperationException(
                    $"The entry '{item.Id}' of the menu '{Name}' opens a submenu and cannot also have an action.");
            }

            Validate(item.Children, depth + 1, ids);
        }
    }

    private static HackMenuItemDefinition? Find(IEnumerable<HackMenuGroupDefinition> groups, string id)
    {
        foreach (HackMenuItemDefinition item in groups.SelectMany(group => group.Items))
        {
            if (string.Equals(item.Id, id, StringComparison.Ordinal))
            {
                return item;
            }

            if (Find(item.Children, id) is { } child)
            {
                return child;
            }
        }

        return null;
    }
}

/// <summary>A group of entries: the OS draws a separator between two visible groups (ADR 0150 §3).</summary>
public sealed class HackMenuGroupDefinition
{
    /// <summary>Gets the group's entries, in order.</summary>
    public IList<HackMenuItemDefinition> Items { get; } = [];

    /// <summary>Adds an entry at the end of the group and returns it.</summary>
    /// <param name="id">The entry id, unique in the menu.</param>
    /// <param name="title">The text shown, already translated by the app.</param>
    public HackMenuItemDefinition Add(string id, string title)
    {
        HackMenuItemDefinition item = new(id, title);
        Items.Add(item);
        return item;
    }

    /// <summary>Copies the group and its entries.</summary>
    public HackMenuGroupDefinition Clone()
    {
        HackMenuGroupDefinition copy = new();
        foreach (HackMenuItemDefinition item in Items)
        {
            copy.Items.Add(item.Clone());
        }

        return copy;
    }
}

/// <summary>
/// One entry of a context menu (ADR 0150 §2). An entry either runs <see cref="OnClick"/> or, when
/// it has <see cref="Children"/>, opens a submenu.
/// </summary>
public sealed class HackMenuItemDefinition
{
    /// <summary>Initializes an entry.</summary>
    /// <param name="id">The entry id, unique in the menu.</param>
    /// <param name="title">The text shown, already translated by the app.</param>
    public HackMenuItemDefinition(string id, string title)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Title = title ?? throw new ArgumentNullException(nameof(title));
    }

    /// <summary>Gets the entry id, unique in the menu; the anchor other entries are placed against.</summary>
    public string Id { get; }

    /// <summary>Gets or sets the text shown, already translated by the app.</summary>
    public string Title { get; set; }

    /// <summary>Gets or sets the icon drawn before the title.</summary>
    public HackIcon? Icon { get; set; }

    /// <summary>Gets or sets whether the entry is shown but cannot be chosen.</summary>
    public bool IsDisabled { get; set; }

    /// <summary>Gets or sets whether the entry is shown; a hidden entry takes no place, nor its separator.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>Gets or sets a shortcut shown at the end of the entry, as text only (the menu binds no key).</summary>
    public string? Shortcut { get; set; }

    /// <summary>
    /// Gets or sets the action, receiving the object attached to the element the menu was opened on
    /// (<c>Menus.Attach(name, item)</c>). It runs in the app's own context, as an <c>@onclick</c>
    /// handler of the component that declared it.
    /// </summary>
    public EventCallback<object?> OnClick { get; set; }

    /// <summary>Gets the submenu's groups; an entry with children has no action.</summary>
    public IList<HackMenuGroupDefinition> Children { get; } = [];

    /// <summary>Copies the entry and its submenu.</summary>
    public HackMenuItemDefinition Clone()
    {
        HackMenuItemDefinition copy = new(Id, Title)
        {
            Icon = Icon,
            IsDisabled = IsDisabled,
            IsVisible = IsVisible,
            Shortcut = Shortcut,
            OnClick = OnClick,
        };
        foreach (HackMenuGroupDefinition group in Children)
        {
            copy.Children.Add(group.Clone());
        }

        return copy;
    }
}

/// <summary>What a menu's <c>Opening</c> handler receives before the OS draws it (ADR 0150 §4).</summary>
public sealed class HackMenuOpeningEventArgs
{
    /// <summary>Initializes the arguments.</summary>
    /// <param name="menu">This opening's own copy of the menu.</param>
    /// <param name="item">The object attached to the element, or <see langword="null"/>.</param>
    public HackMenuOpeningEventArgs(HackMenuDefinition menu, object? item)
    {
        Menu = menu ?? throw new ArgumentNullException(nameof(menu));
        Item = item;
    }

    /// <summary>Gets this opening's copy of the menu: changes to it last for this opening only.</summary>
    public HackMenuDefinition Menu { get; }

    /// <summary>Gets the object attached to the element the menu was opened on.</summary>
    public object? Item { get; }

    /// <summary>Gets or sets whether the menu stays closed.</summary>
    public bool Cancel { get; set; }
}
