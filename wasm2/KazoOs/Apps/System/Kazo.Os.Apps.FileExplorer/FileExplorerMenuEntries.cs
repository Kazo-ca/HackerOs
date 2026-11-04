namespace Kazo.Os.Apps.FileExplorer;

/// <summary>
/// The File Explorer's entry in the file menus of every host (ADR 0151 §8): "Extract here" on
/// <c>.zip</c> files, registered by <see cref="FileExplorerApp.RegisterFileContextMenu"/>. The OS
/// launches the explorer with <see cref="ExtractHereIntent"/>; the new window opens the archive's folder
/// and extracts each archive next to itself, like the toolbar's Extract.
/// </summary>
internal static class FileExplorerMenuEntries
{
    /// <summary>The entry's name in the explorer's registration.</summary>
    internal const string ExtractHere = "extract-here";

    /// <summary>The intent the OS hands the explorer for "Extract here", with <c>{"paths":[…]}</c>.</summary>
    internal const string ExtractHereIntent = "org.kazoos.file-explorer.extract-here.v1";

    /// <summary>The resource key of the entry's title (<c>Resources/AppManifest*.resx</c>, ADR 0159 §3).</summary>
    internal const string ExtractHereTitle = "Menu.ExtractHere";
}
