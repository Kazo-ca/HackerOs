namespace HackerOs.AppSdk.Files;

/// <summary>
/// Where a file menu opens (ADR 0151 §1): on a file, on a folder, in the empty space of a folder,
/// or in the empty space of the desktop. Combinable, for an entry that applies to several.
/// </summary>
[Flags]
public enum FileMenuScope
{
    /// <summary>No scope.</summary>
    None = 0,

    /// <summary>A right click on a file.</summary>
    File = 1,

    /// <summary>A right click on a folder.</summary>
    Directory = 2,

    /// <summary>A right click in the empty space of a folder view (the "background").</summary>
    Background = 4,

    /// <summary>A right click in the empty space of the desktop: the background menu plus the desktop's own entries (ADR 0152).</summary>
    Desktop = 8
}

/// <summary>
/// The ids of the entries the file control puts in its menus (plan §7, ADR 0151 §1). They are short
/// and stable: an app places its own entries against them (<c>after: HackFileMenuItems.Copy</c>), hides
/// or disables them in <c>ContextMenuOpening</c>, and runs the same commands from its toolbar through
/// <c>IHackFileView.ExecuteAsync</c>. They belong to the SDK's SemVer promise (ADR 0114 §2).
/// </summary>
public static class HackFileMenuItems
{
    /// <summary>Opens the item with its default app, or enters the folder.</summary>
    public const string Open = "open";

    /// <summary>The "Open with" submenu: the apps that declare the type, the default first.</summary>
    public const string OpenWith = "open-with";

    /// <summary>"Choose another app…" at the end of the "Open with" submenu: the provided dialog with "Always use".</summary>
    public const string OpenWithOther = "open-with-other";

    /// <summary>
    /// Prefix of the ids of the apps listed in the "Open with" submenu: <c>open-with:&lt;appId&gt;</c>.
    /// </summary>
    public const string OpenWithAppPrefix = "open-with:";

    /// <summary>Cuts the selection to the session clipboard.</summary>
    public const string Cut = "cut";

    /// <summary>Copies the selection to the session clipboard.</summary>
    public const string Copy = "copy";

    /// <summary>Pastes the clipboard's files into the folder shown (background menu).</summary>
    public const string Paste = "paste";

    /// <summary>Pastes the clipboard's files into the folder right-clicked.</summary>
    public const string PasteInto = "paste-into";

    /// <summary>Renames the item in place.</summary>
    public const string Rename = "rename";

    /// <summary>Moves the selection to the trash.</summary>
    public const string Delete = "delete";

    /// <summary>Deletes the selection for good, after a confirmation.</summary>
    public const string DeletePermanently = "delete-permanently";

    /// <summary>Creates a folder in the folder shown and renames it in place.</summary>
    public const string NewFolder = "new-folder";

    /// <summary>Reads the folder again.</summary>
    public const string Refresh = "refresh";

    /// <summary>Shows the properties of the item, or of the folder shown (background menu).</summary>
    public const string Properties = "properties";

    /// <summary>The desktop's "View" submenu (ADR 0152).</summary>
    public const string View = "view";

    /// <summary>The desktop's "Sort by" submenu (ADR 0152).</summary>
    public const string Sort = "sort";

    /// <summary>The desktop's "Background…" entry, which opens Settings (ADR 0152).</summary>
    public const string Background = "background";

    /// <summary>"Empty Trash" in the menu of the desktop's Trash icon, after a confirmation (ADR 0152 §4).</summary>
    public const string EmptyTrash = "empty-trash";
}

/// <summary>
/// How an entry an app registers in the file menus handles a selection of several items
/// (ADR 0151 §7).
/// </summary>
public enum MenuSelection
{
    /// <summary>One launch of the target with every selected path (default).</summary>
    All,

    /// <summary>One launch of the target per selected path.</summary>
    Each,

    /// <summary>The entry is hidden when several items are selected.</summary>
    Single
}

/// <summary>
/// How many screens must be open for an entry an app registers in the file menus to appear
/// (ADR 0218 §2). Entries are written at build time; the OS applies the filter each time it draws a
/// menu, from the screens open at that moment.
/// </summary>
public enum MenuScreens
{
    /// <summary>The entry appears whatever the number of screens (default).</summary>
    Any,

    /// <summary>The entry appears only while screen 1 is the only open screen.</summary>
    Single,

    /// <summary>The entry appears only while two screens or more are open.</summary>
    Multiple
}
