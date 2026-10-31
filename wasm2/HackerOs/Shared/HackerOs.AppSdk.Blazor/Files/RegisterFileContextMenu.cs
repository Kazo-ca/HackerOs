using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Files;
using HackerOs.AppSdk.Icons;

namespace HackerOs.AppSdk.Blazor.Files;

/// <summary>
/// Marks the static method of an <c>[AppManifest]</c> class that registers the app's entries in the
/// file menus of the OS (ADR 0151 §2). The method takes one <see cref="RegisterFileContextMenuContext"/>
/// and returns nothing; the manifest generator runs it once, at build, and writes what it registered
/// to <c>manifest.json</c>. It never runs in the OS: it may not read files, call services or depend
/// on the time, and two runs must register the same entries.
/// </summary>
/// <example>
/// <code>
/// [RegisterFileContextMenu]
/// public static void RegisterFileContextMenu(RegisterFileContextMenuContext context)
/// {
///     context.Register(title: "Menu.Extract", name: "extract", intent: "com.example.zip.extract.v1",
///         types: [".zip"], selection: MenuSelection.Each, afterGroup: HackFileMenuItems.Copy);
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RegisterFileContextMenuAttribute : Attribute
{
}

/// <summary>
/// Records the entries an app registers in the file menus (ADR 0151 §2). It only records the calls:
/// it gives access to no service, file or network. The app needs the <c>shell.context-menu</c>
/// capability; the generator checks every rule of ADR 0151 §5 and fails the build otherwise.
/// </summary>
public sealed class RegisterFileContextMenuContext
{
    private readonly List<AppContextMenuEntryManifest> _entries = [];

    /// <summary>
    /// Gets the entries registered so far, in declaration order, as the manifest describes them
    /// (titles are resolved later, by the generator, from the app's resources). A child entry
    /// registered without a scope takes its parent's.
    /// </summary>
    public IReadOnlyList<AppContextMenuEntryManifest> Entries =>
        [.. _entries.Select(entry => entry.Parent is { } parent && _inheritScope.Contains(entry.Name)
            ? entry with { Scopes = ScopesOf(parent, depth: 0) }
            : entry)];

    private readonly HashSet<string> _inheritScope = new(StringComparer.Ordinal);

    /// <summary>Registers one entry.</summary>
    /// <param name="title">
    /// The resource key of the title: the English text lives in the app's <c>Resources/AppManifest.resx</c>,
    /// a translation in <c>Resources/AppManifest.&lt;culture&gt;.resx</c> (ADR 0159 §3).
    /// </param>
    /// <param name="name">The entry's id, unique in the app, in lower-case words joined by dashes.</param>
    /// <param name="target"><c>"self"</c>, or the id of another app that declares <paramref name="intent"/>.</param>
    /// <param name="intent">
    /// The intent handed to the target with the paths (<c>{"paths":[…],"scope":"File"}</c>); when the
    /// target does not declare it, the launch arguments <c>["&lt;intent&gt;", "&lt;path&gt;", …]</c>.
    /// Leave it out on an entry that opens a submenu.
    /// </param>
    /// <param name="icon">The icon: <c>HackIcons.Vector(…)</c>, or <c>HackIcons.FromImage(…)</c> with an asset of the app.</param>
    /// <param name="scope">The menus the entry appears in; <see cref="FileMenuScope.File"/> by default, the parent's for a child entry.</param>
    /// <param name="types">Extensions (<c>.zip</c>), media types (<c>application/zip</c>), families (<c>image/*</c>) or <c>*</c>; every type when left out.</param>
    /// <param name="selection">How a selection of several items is handled.</param>
    /// <param name="first">Puts the entry in the shared group at the top of the menu.</param>
    /// <param name="after">Puts the entry right after this id, in its group (<see cref="HackFileMenuItems"/> or an entry of the app).</param>
    /// <param name="afterGroup">Puts the entry in a new group right after the group of this id.</param>
    /// <param name="parent">Puts the entry in the submenu of this entry of the app (three levels at most).</param>
    public void Register(
        string title,
        string name,
        string target = AppContextMenus.SelfTarget,
        string? intent = null,
        HackIcon? icon = null,
        FileMenuScope scope = FileMenuScope.None,
        IReadOnlyList<string>? types = null,
        MenuSelection selection = MenuSelection.All,
        bool first = false,
        string? after = null,
        string? afterGroup = null,
        string? parent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        if (scope == FileMenuScope.None && parent is not null)
        {
            _inheritScope.Add(name);
        }

        _entries.Add(new AppContextMenuEntryManifest
        {
            Name = name,
            Title = title,
            Target = target,
            Intent = intent,
            Icon = icon is null ? null : ToManifest(icon),
            Scopes = ToScopes(scope == FileMenuScope.None ? FileMenuScope.File : scope),
            Types = types is null ? null : [.. types.Select(type => type.Trim().ToLowerInvariant())],
            Selection = selection switch
            {
                MenuSelection.Each => AppContextMenus.SelectionEach,
                MenuSelection.Single => AppContextMenus.SelectionSingle,
                _ => AppContextMenus.SelectionAll
            },
            First = first,
            After = after,
            AfterGroup = afterGroup,
            Parent = parent
        });
    }

    /// <summary>
    /// Shows a registered entry only for a number of open screens (ADR 0218 §2): a plain entry with
    /// one screen, a submenu of screens with several.
    /// </summary>
    /// <param name="name">The name of an entry registered before.</param>
    /// <param name="screens"><see cref="MenuScreens.Single"/> or <see cref="MenuScreens.Multiple"/>; <see cref="MenuScreens.Any"/> removes the filter.</param>
    /// <exception cref="ArgumentException">No entry has that name.</exception>
    public void RestrictToScreens(string name, MenuScreens screens) => Update(name, entry => entry with
    {
        Screens = screens switch
        {
            MenuScreens.Single => AppContextMenus.ScreensSingle,
            MenuScreens.Multiple => AppContextMenus.ScreensMultiple,
            _ => null
        }
    });

    /// <summary>
    /// Shows a registered entry only while one secondary screen is open (ADR 0218 §2), for example
    /// « Screen 3 » in a submenu of screens.
    /// </summary>
    /// <param name="name">The name of an entry registered before.</param>
    /// <param name="screen">The screen number, 2 to <see cref="AppContextMenus.MaxScreenNumber"/> (the generator checks it).</param>
    /// <exception cref="ArgumentException">No entry has that name.</exception>
    public void RestrictToScreen(string name, int screen) => Update(name, entry => entry with { Screen = screen });

    /// <summary>Converts the SDK's scope flags to the manifest's names, in menu order.</summary>
    /// <param name="scope">The flags.</param>
    public static IReadOnlyList<string> ToScopes(FileMenuScope scope)
    {
        List<string> scopes = [];
        if (scope.HasFlag(FileMenuScope.File))
        {
            scopes.Add(AppContextMenus.FileScope);
        }

        if (scope.HasFlag(FileMenuScope.Directory))
        {
            scopes.Add(AppContextMenus.DirectoryScope);
        }

        if (scope.HasFlag(FileMenuScope.Background))
        {
            scopes.Add(AppContextMenus.BackgroundScope);
        }

        if (scope.HasFlag(FileMenuScope.Desktop))
        {
            scopes.Add(AppContextMenus.DesktopScope);
        }

        return scopes;
    }

    /// <summary>Replaces a registered entry by a changed copy (the manifest records are immutable).</summary>
    private void Update(string name, Func<AppContextMenuEntryManifest, AppContextMenuEntryManifest> change)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        int index = _entries.FindIndex(entry => entry.Name == name);
        if (index < 0)
        {
            throw new ArgumentException($"No menu entry named '{name}' was registered before.", nameof(name));
        }

        _entries[index] = change(_entries[index]);
    }

    private IReadOnlyList<string> ScopesOf(string name, int depth)
    {
        AppContextMenuEntryManifest? entry = _entries.FirstOrDefault(item => item.Name == name);
        if (entry is null)
        {
            return [AppContextMenus.FileScope];
        }

        // A parent registered without a scope inherits in turn; a cycle stops at MaxDepth (the validator reports it).
        return entry.Parent is { } parent && _inheritScope.Contains(entry.Name) && depth < AppContextMenus.MaxDepth
            ? ScopesOf(parent, depth + 1)
            : entry.Scopes;
    }

    private static AppContextMenuIconManifest ToManifest(HackIcon icon) => icon.Kind == HackIconKind.Vector
        ? new AppContextMenuIconManifest(AppContextMenus.VectorIcon, icon.Source, icon.Library.ToString().ToLowerInvariant())
        : new AppContextMenuIconManifest(AppContextMenus.ImageIcon, icon.Source);
}
