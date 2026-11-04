using Microsoft.AspNetCore.Components;

namespace HackerOs.AppSdk.Blazor.ContextMenus;

/// <summary>
/// The attribute names <see cref="HackMenus.Attach"/> puts on an element (ADR 0150 §4); the OS
/// script looks for the nearest ancestor carrying them when the user asks for a menu.
/// </summary>
public static class HackMenuAttributes
{
    /// <summary>The menu's name.</summary>
    public const string Menu = "data-hack-menu";

    /// <summary>The opaque token naming the attached object, kept on the C# side.</summary>
    public const string Token = "data-hack-menu-token";

    /// <summary>
    /// Marks an OS area that has its own menu (desktop, taskbar): the browser's menu is not shown
    /// there even where no entry is attached.
    /// </summary>
    public const string Suppress = "data-hack-menu-suppress";
}

/// <summary>A menu registered in a window, under its name.</summary>
/// <param name="Name">The menu's name, unique in its window.</param>
/// <param name="Build">Builds a fresh copy of the menu each time it opens.</param>
/// <param name="Opening">Runs before the menu is drawn, with that opening's copy and the attached object.</param>
public sealed record HackMenuRegistration(
    string Name,
    Func<HackMenuDefinition> Build,
    EventCallback<HackMenuOpeningEventArgs> Opening);

/// <summary>
/// The OS side of a window's context menus (ADR 0150 §4), implemented by the platform and given to
/// the window's components in cascade. Apps use it through <see cref="HackMenus"/>
/// (<c>Menus</c> on <see cref="HackComponentBase"/> and <see cref="WindowAppBase"/>).
/// </summary>
public interface IHackMenuHost
{
    /// <summary>
    /// Returns the attributes that make an element open the menu <paramref name="name"/> for
    /// <paramref name="item"/>. The object stays on the C# side, under an opaque token; the same
    /// object attached again gets the same token.
    /// </summary>
    /// <param name="name">The menu's name.</param>
    /// <param name="item">The object handed to the menu's actions, or <see langword="null"/>.</param>
    IReadOnlyDictionary<string, object> Attach(string name, object? item);

    /// <summary>Registers a menu under its name; disposing the result removes it.</summary>
    /// <param name="registration">The menu.</param>
    /// <exception cref="InvalidOperationException">A menu of the same name is already registered in this window.</exception>
    IDisposable Register(HackMenuRegistration registration);

    /// <summary>Opens a registered menu at a point of the viewport, as a right click there would.</summary>
    /// <param name="name">The menu's name.</param>
    /// <param name="item">The object handed to the menu's actions.</param>
    /// <param name="clientX">Horizontal position, in CSS pixels from the viewport's left.</param>
    /// <param name="clientY">Vertical position, in CSS pixels from the viewport's top.</param>
    /// <returns>Whether a menu opened (known name, not cancelled, at least one visible entry).</returns>
    Task<bool> OpenAsync(string name, object? item, double clientX, double clientY);

    /// <summary>Closes the OS menu when it was opened from this window.</summary>
    void Close();
}

/// <summary>
/// The controller of the contract <c>hackeros.context-menu</c> (ADR 0150 §1), handed by the OS once
/// a <see cref="HackContextMenu"/> is registered.
/// </summary>
public interface IHackContextMenu
{
    /// <summary>Gets the menu's name.</summary>
    string Name { get; }

    /// <summary>Opens the menu at a point of the viewport for <paramref name="item"/>.</summary>
    /// <param name="item">The object handed to the menu's actions.</param>
    /// <param name="clientX">Horizontal position, in CSS pixels.</param>
    /// <param name="clientY">Vertical position, in CSS pixels.</param>
    /// <returns>Whether a menu opened.</returns>
    Task<bool> OpenAsync(object? item, double clientX, double clientY);

    /// <summary>Closes the menu when it is open.</summary>
    void Close();
}

/// <summary>
/// A window's context menus, as a component sees them (<c>Menus</c>, ADR 0150 §4): attach a menu to
/// any element, define one in C#, or open one from code. Outside a KazoOS window (a unit test),
/// attaching returns no attribute and nothing opens.
/// </summary>
/// <example>
/// <code>
/// &lt;tr @attributes="Menus.Attach("files", row)"&gt;…&lt;/tr&gt;
/// </code>
/// </example>
public sealed class HackMenus
{
    private static readonly IReadOnlyDictionary<string, object> NoAttributes = new Dictionary<string, object>();
    private readonly IHackMenuHost? _host;
    private readonly object? _receiver;

    /// <summary>Initializes the view of a window's menus for one component.</summary>
    /// <param name="host">The window's menus, or <see langword="null"/> outside a window.</param>
    /// <param name="receiver">The component that owns callbacks defined through <see cref="Define"/>.</param>
    public HackMenus(IHackMenuHost? host, object? receiver)
    {
        _host = host;
        _receiver = receiver;
    }

    /// <summary>Gets the window's menus this view uses.</summary>
    internal IHackMenuHost? Host => _host;

    /// <summary>Gets whether the OS menus are reachable (inside a KazoOS window).</summary>
    public bool IsAvailable => _host is not null;

    /// <summary>
    /// Returns the attributes to splat on an element (<c>@attributes</c>) so that a right click, a long
    /// press, the Menu key or Shift+F10 on it opens the menu <paramref name="name"/> for <paramref name="item"/>.
    /// </summary>
    /// <param name="name">The menu's name.</param>
    /// <param name="item">The object handed to the menu's actions, or <see langword="null"/>.</param>
    public IReadOnlyDictionary<string, object> Attach(string name, object? item = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _host?.Attach(name, item) ?? NoAttributes;
    }

    /// <summary>
    /// Defines a menu in C#, with the same model as <see cref="HackContextMenu"/>. Disposing the result
    /// removes it; the window's closing removes it too.
    /// </summary>
    /// <param name="name">The menu's name, unique in the window.</param>
    /// <param name="build">Fills the menu; called again each time it opens, so titles stay current.</param>
    /// <param name="opening">Optional: adjusts the menu for the attached object before it is drawn.</param>
    /// <exception cref="InvalidOperationException">A menu of the same name is already defined in this window.</exception>
    public IDisposable Define(string name, Action<HackMenuBuilder> build, Func<HackMenuOpeningEventArgs, Task>? opening = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(build);
        if (_host is null)
        {
            return NoRegistration.Instance;
        }

        object? receiver = _receiver;
        EventCallback<HackMenuOpeningEventArgs> openingCallback = opening is null
            ? default
            : receiver is null
                ? new EventCallback<HackMenuOpeningEventArgs>(null, opening)
                : EventCallback.Factory.Create<HackMenuOpeningEventArgs>(receiver, opening);
        return _host.Register(new HackMenuRegistration(
            name,
            () =>
            {
                HackMenuDefinition menu = new(name);
                build(new HackMenuBuilder(menu, receiver));
                return menu;
            },
            openingCallback));
    }

    /// <summary>Opens a menu at a point of the viewport (a "⋯" button, say), as a right click there would.</summary>
    /// <param name="name">The menu's name.</param>
    /// <param name="item">The object handed to the menu's actions.</param>
    /// <param name="clientX">Horizontal position, in CSS pixels.</param>
    /// <param name="clientY">Vertical position, in CSS pixels.</param>
    /// <returns>Whether a menu opened.</returns>
    public Task<bool> OpenAsync(string name, object? item, double clientX, double clientY) =>
        _host?.OpenAsync(name, item, clientX, clientY) ?? Task.FromResult(false);

    /// <summary>Closes the OS menu when it was opened from this window.</summary>
    public void Close() => _host?.Close();

    private sealed class NoRegistration : IDisposable
    {
        public static readonly NoRegistration Instance = new();

        public void Dispose()
        {
        }
    }
}
