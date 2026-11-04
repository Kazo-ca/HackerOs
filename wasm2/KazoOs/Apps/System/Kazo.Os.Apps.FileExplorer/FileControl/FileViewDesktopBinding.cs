using Kazo.Os.AppSdk;
using Kazo.Os.Apps.FileExplorer.FileControl.Events;
using Kazo.Os.AppSdk.Files;

namespace Kazo.Os.Apps.FileExplorer.FileControl;

/// <summary>
/// What the desktop mode of <see cref="FileView"/> needs from its provider, the File Explorer (ADR 0160):
/// the <c>[view]</c> options and positions its host passed to <c>HackFileView</c>, where to send new
/// positions, and the behavior of the Home and Trash system icons, which are not files.
/// </summary>
/// <remarks>
/// Internal, like <see cref="FileViewMenuBinding"/>: only the explorer binds the view. The properties
/// are read on each render, so the explorer may change them when its host's parameters change.
/// </remarks>
internal sealed class FileViewDesktopBinding
{
    private static readonly IReadOnlyDictionary<string, object> NoAttributes = new Dictionary<string, object>();

    /// <summary>Gets or sets the <c>[view]</c> options: icon size, auto-arrange, alignment, sort, system icons.</summary>
    public HackDesktopOptions Options { get; set; } = HackDesktopOptions.Default;

    /// <summary>Gets or sets the saved cell of each item, by file name or system key.</summary>
    public IReadOnlyDictionary<string, HackDesktopCell> Positions { get; set; } = new Dictionary<string, HackDesktopCell>();

    /// <summary>Gets or sets what receives the positions to save (the host's <c>DesktopPositionsChanged</c>).</summary>
    public Func<IReadOnlyDictionary<string, HackDesktopCell>, Task> SavePositions { get; set; } = static _ => Task.CompletedTask;

    /// <summary>Gets or sets the label of the Home icon, in the host's language.</summary>
    public string HomeLabel { get; set; } = "Home";

    /// <summary>Gets or sets the label of the Trash icon, in the host's language.</summary>
    public string TrashLabel { get; set; } = "Trash";

    /// <summary>Gets or sets the accessible name of the desktop grid.</summary>
    public string GridLabel { get; set; } = "Desktop";

    /// <summary>Gets or sets whether the trash holds something: its icon is drawn full.</summary>
    public bool IsTrashFull { get; set; }

    /// <summary>Gets or sets the attributes that open the OS menu of a system icon (<see cref="HackDesktopItems"/> key).</summary>
    public Func<string, IReadOnlyDictionary<string, object>> AttachSystemMenu { get; set; } = static _ => NoAttributes;

    /// <summary>Gets or sets what opens a system icon (double click, Enter).</summary>
    public Func<string, Task> OpenSystemItem { get; set; } = static _ => Task.CompletedTask;

    /// <summary>Gets or sets what receives a drop on a system icon: into the home folder, or to the trash.</summary>
    public Func<string, FileViewDropRequestedEventArgs, Task> DropOnSystemItem { get; set; } = static (_, _) => Task.CompletedTask;
}
