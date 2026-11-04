using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.AppSdk.Components;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Settings;

/// <summary>
/// Settings &gt; File menus (ADR 0159 §8): the entries apps added to the file menus (ADR 0151), grouped
/// by app, each with a switch that hides or shows it for the session user.
/// </summary>
/// <remarks>
/// Uses only <see cref="HackFileMenuEntries"/> (capability <c>apps.manage</c>). The choice is written
/// to the user's <c>~/.config/hackeros/context-menus.config</c> and applies to the next menu opened.
/// A submenu entry whose parent is hidden is shown greyed: hiding the parent hides the whole submenu.
/// </remarks>
public sealed partial class FileMenuEntriesPanel
{
    private IReadOnlyList<HackFileMenuEntryInfo> _entries = [];
    private bool _isAvailable = true;
    private bool _isBusy;
    private string? _message;
    private IStringLocalizer<Strings>? _localizer;

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>Gets the entries, as last read.</summary>
    internal IReadOnlyList<HackFileMenuEntryInfo> Entries => _entries;

    /// <summary>Gets whether the host lists the entries.</summary>
    internal bool IsAvailable => _isAvailable;

    /// <summary>Gets the text of the last error, or <see langword="null"/>.</summary>
    internal string? Message => _message;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception exception) when (exception is NotSupportedException or UnauthorizedAccessException)
        {
            // A host without the service, or a Settings build without apps.manage.
            _isAvailable = false;
        }
    }

    private async Task RefreshAsync() =>
        _entries = await HackFileMenuEntries.GetEntriesAsync(Context, Context.CancellationToken);

    /// <summary>Shows (<paramref name="visible"/>) or hides one entry, then reads the list again.</summary>
    internal async Task ToggleAsync(HackFileMenuEntryInfo entry, bool visible)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        _message = null;
        try
        {
            await HackFileMenuEntries.SetHiddenAsync(Context, entry.AppId, entry.Name, !visible, Context.CancellationToken);
            await RefreshAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            _message = L["FileMenus.Error.CannotSave", exception.Message];
        }
        finally
        {
            _isBusy = false;
        }
    }

    /// <summary>An app's entries with each submenu right under its parent (depth first, declaration order).</summary>
    internal static IEnumerable<HackFileMenuEntryInfo> Ordered(IEnumerable<HackFileMenuEntryInfo> entries)
    {
        HackFileMenuEntryInfo[] all = [.. entries];
        IEnumerable<HackFileMenuEntryInfo> Children(string? parent) => all
            .Where(entry => entry.Parent == parent)
            .SelectMany(entry => Children(entry.Name).Prepend(entry));

        // Entries whose parent is missing (never in a valid manifest) still show, at the end.
        HackFileMenuEntryInfo[] ordered = [.. Children(null)];
        return ordered.Concat(all.Except(ordered));
    }

    /// <summary>The depth of an entry in its app's submenus, 0 at the top of the menu.</summary>
    internal int DepthOf(HackFileMenuEntryInfo entry)
    {
        int depth = 0;
        for (HackFileMenuEntryInfo? current = entry; current?.Parent is { } parent && depth < 3; depth++)
        {
            current = _entries.FirstOrDefault(other => other.AppId == entry.AppId && other.Name == parent);
        }

        return depth;
    }

    /// <summary>Whether one of the entry's parents is hidden, which hides the entry whatever its own switch.</summary>
    internal bool IsHiddenByParent(HackFileMenuEntryInfo entry)
    {
        string? parent = entry.Parent;
        for (int depth = 0; parent is not null && depth < 3; depth++)
        {
            HackFileMenuEntryInfo? found = _entries.FirstOrDefault(other => other.AppId == entry.AppId && other.Name == parent);
            if (found is null)
            {
                return false;
            }

            if (found.IsHidden)
            {
                return true;
            }

            parent = found.Parent;
        }

        return false;
    }

    private string EntryClass(HackFileMenuEntryInfo entry)
    {
        string css = $"file-menus-entry depth-{DepthOf(entry)}";
        return entry.IsHidden || IsHiddenByParent(entry) ? css + " hidden" : css;
    }

    private string ScopeTitle(string scope) => scope switch
    {
        "file" => L["FileMenus.Scope.File"],
        "directory" => L["FileMenus.Scope.Directory"],
        "background" => L["FileMenus.Scope.Background"],
        "desktop" => L["FileMenus.Scope.Desktop"],
        _ => scope
    };
}
