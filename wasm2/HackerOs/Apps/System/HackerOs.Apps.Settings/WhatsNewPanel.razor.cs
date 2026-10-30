using System.Globalization;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Globalization;
using HackerOs.AppSdk.WhatsNew;
using Microsoft.Extensions.Localization;

namespace HackerOs.Apps.Settings;

/// <summary>
/// Settings &gt; What's new (ADR 0186 §4): what changed in HackerOS, grouped by version, with a
/// <b>New</b> badge on the changes the session user has not seen. The badges stay for the visit and
/// the changes become seen when the page closes (another page chosen, or Settings closed), through
/// <see cref="HackWhatsNew.MarkSeenAsync(IAppExecutionContext, IReadOnlyCollection{string}, CancellationToken)"/>.
/// The panel uses the public SDK only.
/// </summary>
public sealed partial class WhatsNewPanel
{
    private HackChangeList _list = HackChangeList.Empty;
    private IReadOnlyList<string> _newIds = [];
    private bool _loaded;
    private int _markedSeen;
    private IStringLocalizer<Strings>? _localizer;

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>Gets the change list shown.</summary>
    internal HackChangeList List => _list;

    /// <summary>Gets the ids that were new when the page opened: they become seen when it closes.</summary>
    internal IReadOnlyList<string> NewIds => _newIds;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        try
        {
            _list = await HackWhatsNew.GetAsync(Context, Context.CancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or NotSupportedException)
        {
            _list = HackChangeList.Empty;
        }

        _newIds = [.. _list.Groups.SelectMany(static group => group.Entries).Where(static entry => entry.IsNew).Select(static entry => entry.Id)];
        _loaded = true;
    }

    /// <summary>Marks the changes shown as new as seen; called once, when the page closes.</summary>
    internal async Task MarkShownAsSeenAsync()
    {
        if (Interlocked.Exchange(ref _markedSeen, 1) == 1 || _newIds.Count == 0 || AppContext is null)
        {
            return;
        }

        try
        {
            await HackWhatsNew.MarkSeenAsync(AppContext, _newIds, CancellationToken.None);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or NotSupportedException or OperationCanceledException or UnauthorizedAccessException)
        {
            // Settings is closing or the session ended: the badges come back at the next visit.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await MarkShownAsSeenAsync();

    private string GroupTitle(HackChangeGroup group)
    {
        if (group.IsNextVersion)
        {
            return L["WhatsNew.Group.Next", group.Since ?? HackerOsSdk.Version];
        }

        return group.Date is { } date
            ? L["WhatsNew.Group.VersionWithDate", group.Version!, date.ToString("D", FormatCulture)]
            : L["WhatsNew.Group.Version", group.Version!];
    }

    private CultureInfo FormatCulture => AppContext?.FormatCulture ?? CultureInfo.InvariantCulture;

    private string CategoryLabel(HackChangeCategory category) => category switch
    {
        HackChangeCategory.New => L["WhatsNew.Category.New"],
        HackChangeCategory.Improved => L["WhatsNew.Category.Improved"],
        HackChangeCategory.Fixed => L["WhatsNew.Category.Fixed"],
        _ => L["WhatsNew.Category.Developer"]
    };
}
