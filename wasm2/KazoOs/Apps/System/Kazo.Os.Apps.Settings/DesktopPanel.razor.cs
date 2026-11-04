using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Files;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Settings;

/// <summary>
/// Settings &gt; Desktop (ADR 0160 §8): icon size, auto-arrange, alignment, sort and the Home and Trash
/// icons, written to the <c>[view]</c> group of <c>~/.config/hackeros/desktop.config</c> through
/// <see cref="HackDesktopSettings"/>.
/// </summary>
/// <remarks>
/// The desktop app watches the file: a choice made here shows on the desktop at once. Choosing a sort
/// arranges the icons once in that order (the saved positions are cleared), as "Sort by" does in the
/// desktop's menu; "Reset positions" does the same without changing the sort.
/// </remarks>
public sealed partial class DesktopPanel
{
    /// <summary>The icon sizes, in the order shown.</summary>
    internal static readonly HackDesktopIconSize[] IconSizes = [HackDesktopIconSize.Small, HackDesktopIconSize.Medium, HackDesktopIconSize.Large];

    /// <summary>The sorts, in the order shown.</summary>
    internal static readonly HackDesktopSort[] Sorts = [HackDesktopSort.Name, HackDesktopSort.Size, HackDesktopSort.Type, HackDesktopSort.Date];

    private HackDesktopOptions _options = HackDesktopOptions.Default;
    private bool _loaded;
    private bool _isBusy;
    private string? _message;
    private bool _messageIsError;
    private IStringLocalizer<Strings>? _localizer;

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= Context.GetLocalizer<Strings>();

    /// <summary>Gets the options read from the file.</summary>
    internal HackDesktopOptions Options => _options;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        using IDisposable scope = HackExecutionContext.Enter(Context);
        _options = (await HackDesktopSettings.ReadAsync(Context.CancellationToken)).Options;
        _loaded = true;
    }

    /// <summary>Writes <paramref name="options"/> to <c>[view]</c>.</summary>
    internal Task SaveAsync(HackDesktopOptions options) => RunAsync(() => HackDesktopSettings.SaveOptionsAsync(options, Context.CancellationToken), options);

    /// <summary>A new sort arranges the icons once in that order: the positions are cleared with it.</summary>
    internal Task SortAsync(HackDesktopSort sort)
    {
        HackDesktopOptions options = _options with { Sort = sort };
        return RunAsync(async () =>
        {
            await HackDesktopSettings.SaveOptionsAsync(options, Context.CancellationToken);
            await HackDesktopSettings.SavePositionsAsync(new Dictionary<string, HackDesktopCell>(), Context.CancellationToken);
        }, options);
    }

    /// <summary>Clears <c>[positions]</c>: the desktop places every icon again in the sort order.</summary>
    internal Task ResetPositionsAsync() =>
        RunAsync(() => HackDesktopSettings.SavePositionsAsync(new Dictionary<string, HackDesktopCell>(), Context.CancellationToken), _options);

    private async Task RunAsync(Func<Task> write, HackDesktopOptions options)
    {
        _isBusy = true;
        _message = null;
        try
        {
            using IDisposable scope = HackExecutionContext.Enter(Context);
            await write();
            _options = options;
            _message = L["Desktop.Saved"];
            _messageIsError = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _message = L["Desktop.SaveFailed", exception.Message];
            _messageIsError = true;
        }
        finally
        {
            _isBusy = false;
        }
    }

    private string OnOff(bool value) => value ? L["Desktop.On"] : L["Desktop.Off"];

    private static string OptionClass(bool selected) => selected ? "desk-option is-selected" : "desk-option";
}
