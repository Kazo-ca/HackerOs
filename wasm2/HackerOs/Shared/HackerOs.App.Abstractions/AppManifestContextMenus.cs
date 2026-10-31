using System.Collections.Frozen;

namespace HackerOs.App.Abstractions;

/// <summary>
/// One entry an app adds to the file menus of the OS (ADR 0151, ADR 0159), as written to
/// <c>manifest.json</c> (<c>contextMenus</c>) by the manifest generator from the app's
/// <c>[RegisterFileContextMenu]</c> method. The OS copies it to
/// <c>/etc/hackeros/context-menus.d/&lt;appId&gt;.json</c> and builds the menus from that file:
/// no app code runs to draw a menu, like the verbs of the Windows registry.
/// </summary>
public sealed record AppContextMenuEntryManifest
{
    /// <summary>Gets the entry's id, unique in the app (lower-case words joined by dashes); <c>parent</c>, <c>after</c> and <c>afterGroup</c> name it.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the resource key of the title (ADR 0151 §2), kept to find the entry in the app's resources again.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the title in each language, copied from the app's resources at build (ADR 0159 §3):
    /// English (<c>en</c>) always, then every culture of <c>Resources/AppManifest.&lt;culture&gt;.resx</c>
    /// that translates the key.
    /// </summary>
    public IReadOnlyList<AppContextMenuTitleManifest> Titles { get; init; } = [];

    /// <summary>Gets the app launched by the entry: <see cref="AppContextMenus.SelfTarget"/> or another app's id.</summary>
    public string Target { get; init; } = AppContextMenus.SelfTarget;

    /// <summary>
    /// Gets the intent handed to the target (ADR 0096), or <see langword="null"/> on an entry that only
    /// opens a submenu. When the target does not declare it, the OS passes the launch arguments
    /// <c>["&lt;intent&gt;", "&lt;path&gt;", …]</c> instead (ADR 0151 §7).
    /// </summary>
    public string? Intent { get; init; }

    /// <summary>Gets the entry's icon, or <see langword="null"/>.</summary>
    public AppContextMenuIconManifest? Icon { get; init; }

    /// <summary>
    /// Gets the menus the entry appears in (<see cref="AppContextMenus.Scopes"/>: <c>file</c>,
    /// <c>directory</c>, <c>background</c>, <c>desktop</c>). A child entry has its parent's scopes.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; init; } = [AppContextMenus.FileScope];

    /// <summary>
    /// Gets the file types the entry applies to: extensions (<c>.zip</c>), media types
    /// (<c>application/zip</c>), families (<c>image/*</c>) or <c>*</c>; <see langword="null"/>
    /// (omitted) for every type.
    /// </summary>
    public IReadOnlyList<string>? Types { get; init; }

    /// <summary>Gets how a selection of several items is handled: <c>all</c> (one launch), <c>each</c> (one launch per item) or <c>single</c> (hidden for several).</summary>
    public string Selection { get; init; } = AppContextMenus.SelectionAll;

    /// <summary>Gets whether the entry goes to the shared group at the top of the menu.</summary>
    public bool First { get; init; }

    /// <summary>Gets the id the entry follows inside its group, or <see langword="null"/>.</summary>
    public string? After { get; init; }

    /// <summary>Gets the id whose group the entry follows, in a group of its own, or <see langword="null"/>.</summary>
    public string? AfterGroup { get; init; }

    /// <summary>Gets the name of the entry of the same app whose submenu holds this one, or <see langword="null"/>.</summary>
    public string? Parent { get; init; }

    /// <summary>
    /// Gets how many screens must be open for the entry to appear (ADR 0218 §2):
    /// <see cref="AppContextMenus.ScreensSingle"/> (only screen 1), <see cref="AppContextMenus.ScreensMultiple"/>
    /// (two or more), or <see langword="null"/> (omitted) for any count. An OS older than ADR 0218 ignores it.
    /// </summary>
    public string? Screens { get; init; }

    /// <summary>
    /// Gets the number of the secondary screen (2 to <see cref="AppContextMenus.MaxScreenNumber"/>) that
    /// must be open for the entry to appear (ADR 0218 §2), or <see langword="null"/> (omitted).
    /// </summary>
    public int? Screen { get; init; }
}

/// <summary>The title of a registered menu entry in one language.</summary>
/// <param name="Culture">Culture name, <c>en</c> for the English text.</param>
/// <param name="Text">The title in that language.</param>
public sealed record AppContextMenuTitleManifest(string Culture, string Text);

/// <summary>The icon of a registered menu entry, the same description as <c>HackIcon</c> without its rendering.</summary>
/// <param name="Kind"><c>vector</c> or <c>image</c>.</param>
/// <param name="Source">The icon's name in its library, or the image's asset path (relative to the app's <c>wwwroot/</c>) or absolute URI.</param>
/// <param name="Library">The vector library (<c>lucide</c>, <c>bootstrap</c>, <c>fontawesome</c>, <c>simpleicons</c>); <see langword="null"/> for an image.</param>
public sealed record AppContextMenuIconManifest(string Kind, string Source, string? Library = null);

/// <summary>
/// The vocabulary of the registered file menu entries (ADR 0151, ADR 0159): scopes, selections, the
/// ids of the file control's default entries that an entry may be placed against, and the capability.
/// </summary>
public static class AppContextMenus
{
    /// <summary>The target meaning "the app that registers the entry".</summary>
    public const string SelfTarget = "self";

    /// <summary>A right click on a file.</summary>
    public const string FileScope = "file";

    /// <summary>A right click on a folder.</summary>
    public const string DirectoryScope = "directory";

    /// <summary>A right click in the empty space of a folder view.</summary>
    public const string BackgroundScope = "background";

    /// <summary>A right click in the empty space of the desktop.</summary>
    public const string DesktopScope = "desktop";

    /// <summary>One launch with every selected path (default).</summary>
    public const string SelectionAll = "all";

    /// <summary>One launch per selected path.</summary>
    public const string SelectionEach = "each";

    /// <summary>Hidden when several items are selected.</summary>
    public const string SelectionSingle = "single";

    /// <summary>An icon of a bundled vector library.</summary>
    public const string VectorIcon = "vector";

    /// <summary>An image icon.</summary>
    public const string ImageIcon = "image";

    /// <summary>The deepest submenu level of a menu, the entry itself being level 1 (ADR 0150 §2).</summary>
    public const int MaxDepth = 3;

    /// <summary>An entry shown only while a single screen is open (ADR 0218 §2).</summary>
    public const string ScreensSingle = "single";

    /// <summary>An entry shown only while two screens or more are open (ADR 0218 §2).</summary>
    public const string ScreensMultiple = "multiple";

    /// <summary>The lowest screen number an entry can require: screen 1 is always open.</summary>
    public const int MinScreenNumber = 2;

    /// <summary>The highest screen number an entry can require, the limit of <c>screens.config</c> (ADR 0180).</summary>
    public const int MaxScreenNumber = 9;

    /// <summary>The culture of the title every entry must have.</summary>
    public const string EnglishCulture = "en";

    /// <summary>The known scopes, in menu order.</summary>
    public static IReadOnlyList<string> Scopes { get; } = [FileScope, DirectoryScope, BackgroundScope, DesktopScope];

    /// <summary>The known selections.</summary>
    public static IReadOnlyList<string> Selections { get; } = [SelectionAll, SelectionEach, SelectionSingle];

    /// <summary>The vector libraries an icon may name (lower-case <c>IconLibrary</c> names).</summary>
    public static IReadOnlyList<string> VectorLibraries { get; } = ["bootstrap", "fontawesome", "lucide", "simpleicons"];

    /// <summary>
    /// The ids of the file control's default entries (plan §7, ADR 0151 §1): the same values as the
    /// SDK's <c>HackFileMenuItems</c>, which a test keeps equal. An entry may be placed after one of
    /// them, or after an entry of its own app.
    /// </summary>
    public static IReadOnlySet<string> DefaultItemIds { get; } = new[]
    {
        "open", "open-with", "open-with-other", "cut", "copy", "paste", "paste-into", "rename", "delete",
        "delete-permanently", "empty-trash", "new-folder", "refresh", "properties", "view", "sort", "background"
    }.ToFrozenSet(StringComparer.Ordinal);
}
