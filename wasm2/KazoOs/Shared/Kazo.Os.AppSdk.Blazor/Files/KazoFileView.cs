using Kazo.Os.AppSdk.Blazor.Components;
using Kazo.Os.AppSdk.Components;
using Kazo.Os.AppSdk.Files;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.AppSdk.Blazor.Files;

/// <summary>
/// The OS file control (contract <c>kazoos.file-view</c>, ADR 0148): a folder as a table, icons or
/// a tree, with selection, rename in place, drag and drop, the clipboard, the trash, the transfers
/// with their name conflicts, and the file menus of the OS. The File Explorer provides it at run time
/// and shows it itself, so a fix of the control reaches every app without a rebuild; the app only
/// holds this contract.
/// </summary>
/// <remarks>
/// The control runs with the <b>host</b> app's rights (ADR 0148 §4): it lists, copies and deletes what
/// the host may, and its menu commands run in the host's context. Its controller,
/// <see cref="IKazoFileView"/>, comes through <c>ControllerChanged</c>.
/// </remarks>
/// <example>
/// <code>
/// &lt;KazoFileView InitialDirectory="@home" Mode="KazoFileViewMode.Icons"
///               Opening="OnOpening" ControllerChanged="view =&gt; _view = view" /&gt;
/// </code>
/// </example>
public sealed class KazoFileView : KazoContractComponent<IKazoFileView>
{
    /// <summary>Gets or sets the absolute path of the folder shown first.</summary>
    [Parameter, EditorRequired]
    public string InitialDirectory { get; set; } = "/";

    /// <summary>Gets or sets the layout. Two-way bindable.</summary>
    [Parameter]
    public KazoFileViewMode Mode { get; set; } = KazoFileViewMode.Details;

    /// <summary>Gets or sets the callback told when the user changes the layout.</summary>
    [Parameter]
    public EventCallback<KazoFileViewMode> ModeChanged { get; set; }

    /// <summary>Gets or sets what the control does by itself; <see cref="KazoFileViewFeatures.All"/> by default.</summary>
    [Parameter]
    public KazoFileViewFeatures Features { get; set; } = KazoFileViewFeatures.All;

    /// <summary>Gets or sets whether several items can be selected at once.</summary>
    [Parameter]
    public bool AllowMultiSelect { get; set; } = true;

    /// <summary>
    /// Gets or sets whether one click, or one tap, opens an item, as in a phone's file manager
    /// (ADR 0171): a folder is entered, a file is opened (or handed to <see cref="Opening"/>), and a
    /// double click opens nothing more. Ctrl+click still selects. Off by default: a click selects, a
    /// double click opens.
    /// </summary>
    [Parameter]
    public bool OpenOnTap { get; set; }

    /// <summary>
    /// Gets or sets which items are shown (a search box); call <see cref="IKazoFileView.RefreshFilter"/>
    /// when what it returns changes.
    /// </summary>
    [Parameter]
    public Func<KazoFileViewItem, bool>? Filter { get; set; }

    /// <summary>
    /// Gets or sets what runs before an item opens; setting <see cref="KazoFileViewOpeningEventArgs.Handled"/>
    /// keeps the control from opening it (the default app for a file, navigation for a folder).
    /// </summary>
    [Parameter]
    public EventCallback<KazoFileViewOpeningEventArgs> Opening { get; set; }

    /// <summary>Gets or sets the callback told the new folder after each navigation.</summary>
    [Parameter]
    public EventCallback<string> Navigated { get; set; }

    /// <summary>Gets or sets the callback told the new selection.</summary>
    [Parameter]
    public EventCallback<IReadOnlyList<KazoFileViewItem>> SelectionChanged { get; set; }

    /// <summary>Gets or sets the callback told after a rename in place.</summary>
    [Parameter]
    public EventCallback<KazoFileViewRenamedEventArgs> Renamed { get; set; }

    /// <summary>Gets or sets the callback told after items went to the trash or were deleted.</summary>
    [Parameter]
    public EventCallback<KazoFileViewDeletedEventArgs> Deleted { get; set; }

    /// <summary>Gets or sets what runs before a file menu is drawn: hide, disable, add entries, or cancel.</summary>
    [Parameter]
    public EventCallback<KazoFileMenuOpeningEventArgs> ContextMenuOpening { get; set; }

    /// <summary>
    /// Gets or sets the callback receiving the control's errors, worded for the user (a folder that
    /// cannot be read, a copy that failed). Without one, the control posts them as notifications.
    /// </summary>
    [Parameter]
    public EventCallback<string> ErrorOccurred { get; set; }

    /// <summary>
    /// Gets or sets the grant of a folder the user picked in the OS folder dialog
    /// (<c>FileDialogResult.Resource.Handle</c>): the control lists and renames inside it with that
    /// grant, as <c>IAppFileSystemGateway.WithSelectedHandle</c> does, beyond the host's own capabilities.
    /// <see langword="null"/> uses the host's capabilities alone.
    /// </summary>
    [Parameter]
    public FileSystemSelectedResourceHandle? SelectedResource { get; set; }

    /// <summary>
    /// Gets or sets the <c>[view]</c> options of <see cref="KazoFileViewMode.Desktop"/> (ADR 0160 §2):
    /// icon size, auto-arrange, alignment, sort, system icons. <see langword="null"/> uses
    /// <see cref="KazoDesktopOptions.Default"/>. Ignored by the other modes.
    /// </summary>
    [Parameter]
    public KazoDesktopOptions? DesktopOptions { get; set; }

    /// <summary>
    /// Gets or sets the cell of each item in <see cref="KazoFileViewMode.Desktop"/>, by file name
    /// (<see cref="KazoDesktopItems.Home"/> and <see cref="KazoDesktopItems.Trash"/> for the system
    /// icons): the <c>[positions]</c> of <c>desktop.config</c>. The host owns the file; the control
    /// places the items with <see cref="KazoDesktopLayout"/>.
    /// </summary>
    [Parameter]
    public IReadOnlyDictionary<string, KazoDesktopCell>? DesktopPositions { get; set; }

    /// <summary>
    /// Gets or sets the callback told the positions to save in <see cref="KazoFileViewMode.Desktop"/>:
    /// after the user moved icons, and when new items took a cell. The host writes them and passes
    /// them back through <see cref="DesktopPositions"/>.
    /// </summary>
    [Parameter]
    public EventCallback<IReadOnlyDictionary<string, KazoDesktopCell>> DesktopPositionsChanged { get; set; }

    /// <inheritdoc />
    protected override string Contract => KazoComponentContracts.FileView;

    /// <inheritdoc />
    protected override void AddProviderParameters(IDictionary<string, object?> parameters)
    {
        parameters[nameof(InitialDirectory)] = InitialDirectory;
        parameters[nameof(Mode)] = Mode;
        parameters[nameof(ModeChanged)] = ModeChanged;
        parameters[nameof(Features)] = Features;
        parameters[nameof(AllowMultiSelect)] = AllowMultiSelect;
        parameters[nameof(OpenOnTap)] = OpenOnTap;
        parameters[nameof(Filter)] = Filter;
        parameters[nameof(Opening)] = Opening;
        parameters[nameof(Navigated)] = Navigated;
        parameters[nameof(SelectionChanged)] = SelectionChanged;
        parameters[nameof(Renamed)] = Renamed;
        parameters[nameof(Deleted)] = Deleted;
        parameters[nameof(ContextMenuOpening)] = ContextMenuOpening;
        parameters[nameof(ErrorOccurred)] = ErrorOccurred;
        parameters[nameof(SelectedResource)] = SelectedResource;
        parameters[nameof(DesktopOptions)] = DesktopOptions;
        parameters[nameof(DesktopPositions)] = DesktopPositions;
        parameters[nameof(DesktopPositionsChanged)] = DesktopPositionsChanged;
    }
}
