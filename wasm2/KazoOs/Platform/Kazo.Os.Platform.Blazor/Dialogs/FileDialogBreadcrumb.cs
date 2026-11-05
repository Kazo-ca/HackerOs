using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Platform.Blazor.Dialogs;

/// <summary>One step of a file dialog's breadcrumb: a folder from the root down to the one shown.</summary>
/// <param name="Label">The folder's name, <c>/</c> for the root.</param>
/// <param name="Path">The folder's absolute path.</param>
internal sealed record FileDialogBreadcrumb(string Label, VirtualPath Path)
{
    /// <summary>Gets the steps from the root to <paramref name="directory"/>, the root first.</summary>
    /// <param name="directory">The folder shown.</param>
    public static IReadOnlyList<FileDialogBreadcrumb> For(VirtualPath directory)
    {
        List<FileDialogBreadcrumb> breadcrumbs = [new("/", VirtualPath.Parse("/"))];
        string path = string.Empty;
        foreach (string segment in directory.Value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path += $"/{segment}";
            breadcrumbs.Add(new FileDialogBreadcrumb(segment, VirtualPath.Parse(path)));
        }

        return breadcrumbs;
    }
}
