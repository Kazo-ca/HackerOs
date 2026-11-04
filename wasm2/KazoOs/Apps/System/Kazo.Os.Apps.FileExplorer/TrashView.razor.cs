using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Globalization;
using System.Globalization;
using Kazo.Os.AppSdk.IO;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.FileExplorer;

/// <summary>
/// Lists the user's trash through <see cref="HackTrash"/> and restores or permanently deletes its
/// elements (ADR 0052, section 5). Runs in the explorer's ambient app scope (it derives from
/// <see cref="HackComponentBase"/>), so every action is checked like any other file operation of the app.
/// </summary>
public partial class TrashView
{
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private IReadOnlyList<HackTrashItem> _items = [];
    private string? _error;
    private bool _busy;

    /// <summary>The explorer's texts in the language of its process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => AppContext.GetLocalizer<Strings>();

    /// <summary>The culture of the dates and sizes shown, from the explorer's locale.</summary>
    private CultureInfo FormatCulture => AppContext?.FormatCulture ?? HackLocale.DefaultFormatCulture;

    /// <summary>Gets or sets the ecosystem dialog service used for the confirmations.</summary>
    [Inject]
    private IDialogService Dialogs { get; set; } = default!;

    /// <summary>Raised after elements were restored, so the host can refresh the folder they went back to.</summary>
    [Parameter]
    public EventCallback<IReadOnlyList<string>> Restored { get; set; }

    /// <inheritdoc />
    protected override Task OnInitializedAsync() => ReloadAsync();

    /// <summary>Re-reads the trash (called by the explorer each time the view is shown).</summary>
    public async Task ReloadAsync()
    {
        try
        {
            _items = await HackTrash.GetItemsAsync();
            _selected.IntersectWith(_items.Select(item => item.Name));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _items = [];
            _error = L["Trash.ReadError", exception.Message];
        }

        StateHasChanged();
    }

    private void Select(HackTrashItem item, MouseEventArgs args)
    {
        if (!args.CtrlKey && !args.MetaKey)
        {
            _selected.Clear();
            _selected.Add(item.Name);
        }
        else if (!_selected.Remove(item.Name))
        {
            _selected.Add(item.Name);
        }
    }

    private Task OnRowKeyDownAsync(HackTrashItem item, KeyboardEventArgs args)
    {
        switch (args.Key)
        {
            case "Enter":
                return RestoreAsync([item]);
            case "Delete":
                _selected.Add(item.Name);
                return DeleteSelectedAsync();
            case " ":
                Select(item, new MouseEventArgs { CtrlKey = args.CtrlKey });
                break;
        }

        return Task.CompletedTask;
    }

    private IReadOnlyList<HackTrashItem> SelectedItems() => [.. _items.Where(item => _selected.Contains(item.Name))];

    private Task RestoreSelectedAsync() => RestoreAsync(SelectedItems());

    private async Task RestoreAsync(IReadOnlyList<HackTrashItem> items)
    {
        List<string> restored = [];
        await RunAsync(async () =>
        {
            foreach (HackTrashItem item in items)
            {
                // ADR 0069: each item is restored atomically; a stop request ends the loop between two.
                await HackProcess.CheckpointAsync();
                restored.Add(await HackTrash.RestoreAsync(item));
            }
        });

        if (restored.Count > 0)
        {
            await Restored.InvokeAsync(restored);
        }
    }

    private async Task DeleteSelectedAsync()
    {
        IReadOnlyList<HackTrashItem> items = SelectedItems();
        if (items.Count == 0 || !await ConfirmAsync(
                L["Dialog.DeletePermanently.Title"],
                items.Count == 1
                    ? L["Dialog.DeletePermanently.One", DisplayName(items[0])]
                    : L["Dialog.DeletePermanently.Many", items.Count]))
        {
            return;
        }

        await RunAsync(async () =>
        {
            foreach (HackTrashItem item in items)
            {
                await HackProcess.CheckpointAsync();
                await HackTrash.DeleteAsync(item);
            }
        });
    }

    private async Task EmptyAsync()
    {
        if (!await ConfirmAsync(L["Trash.Empty"], L["Trash.EmptyConfirm", _items.Count]))
        {
            return;
        }

        await RunAsync(() => HackTrash.EmptyAsync());
    }

    /// <summary>Runs one action, shows its error if any, then re-reads the trash.</summary>
    private async Task RunAsync(Func<Task> action)
    {
        _busy = true;
        _error = null;
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _error = exception.Message;
        }
        catch (OperationCanceledException)
        {
            // The app was asked to stop (ADR 0069): the items done so far stay done, the rest stay in the trash.
            _error = L["Trash.Stopped"];
        }
        finally
        {
            _busy = false;
            await ReloadAsync();
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        if (AppContext is null)
        {
            return false;
        }

        MessageBoxDialogResult result = await Dialogs.MessageBoxAsync(
            AppContext,
            new MessageBoxDialogRequest { Title = title, Content = message, DialogType = MessageBoxType.YesNo });
        return result.Result == MessageBoxResult.Yes;
    }

    /// <summary>Displays the original file name; falls back to the trash name when the origin is unknown.</summary>
    internal static string DisplayName(HackTrashItem item) =>
        item.OriginalPath is { } path ? HackPath.GetFileName(path) ?? item.Name : item.Name;

    /// <summary>The folder the element was deleted from, or <see langword="null"/> when the origin is unknown.</summary>
    internal static string? OriginalLocation(HackTrashItem item) =>
        item.OriginalPath is { } path ? HackPath.GetDirectoryName(path) ?? "/" : null;

    /// <summary>The deletion date as a short date and time of <paramref name="culture"/>.</summary>
    internal static string FormatDate(DateTimeOffset? deletedAt, CultureInfo culture) =>
        deletedAt?.ToString("g", culture) ?? "—";

    /// <summary>A size in B, KB, MB or GB (o, Ko, Mo, Go in French), with the decimal separator of <paramref name="culture"/>.</summary>
    internal static string FormatBytes(long bytes, CultureInfo culture, IStringLocalizer text)
    {
        string[] units = [text["Size.Bytes"], text["Size.Kilobytes"], text["Size.Megabytes"], text["Size.Gigabytes"]];
        double size = bytes;
        int unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size.ToString("0.#", culture)} {units[unitIndex]}";
    }
}
