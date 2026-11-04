using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.IO;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Settings;

/// <summary>One file type or URI scheme and action, with the apps that can handle it (ADR 0121, section 4).</summary>
/// <param name="Target">Extension (<c>.md</c>) or media type (<c>x-scheme-handler/https</c>), lowercase.</param>
/// <param name="Action"><c>open</c> or <c>edit</c>.</param>
/// <param name="Candidates">Installed window apps that declare it, by name.</param>
/// <param name="UserAppId">The user's own default, when one is set and still installed.</param>
/// <param name="SystemAppId">The administrator's default, when one is set and still installed.</param>
internal sealed record FileAssociationRow(
    string Target, string Action, IReadOnlyList<KazoAppInfo> Candidates, string? UserAppId, string? SystemAppId)
{
    /// <summary>Gets a stable key for rendering.</summary>
    public string Key => Target + "|" + Action;

    /// <summary>Gets what a reader recognizes: <c>https:</c> for a scheme, the target itself otherwise.</summary>
    public string Label => Target.StartsWith(FileAssociationsPanel.SchemePrefix, StringComparison.Ordinal)
        ? Target[FileAssociationsPanel.SchemePrefix.Length..] + ":"
        : Target;

    /// <summary>Gets the app the OS uses without a user choice: the system default, else the only candidate.</summary>
    public string? FallbackAppId => SystemAppId ?? (Candidates.Count == 1 ? Candidates[0].Id : null);
}

/// <summary>
/// Settings &gt; File Associations (ADR 0121, section 4): lists every file type and URI scheme the
/// installed window apps declare and lets the user pick the app that opens each one for them.
/// </summary>
/// <remarks>
/// A pick is written with <see cref="KazoApps.SetDefaultHandlerAsync"/> to the user's
/// <c>~/.config/hackeros/file-associations.json</c>, like GNOME's "Default Applications" writing
/// <c>~/.config/mimeapps.list</c>; the first entry, "system default", removes the user's choice with
/// <see cref="KazoApps.ResetDefaultHandlerAsync"/>. The machine-wide file is shown, never written:
/// it stays an administrator's job (<c>sudo nano /etc/hackeros/file-associations.json</c>).
/// </remarks>
public sealed partial class FileAssociationsPanel
{
    /// <summary>Prefix of the media type of a URI scheme (ADR 0096, section 3).</summary>
    internal const string SchemePrefix = "x-scheme-handler/";

    private IReadOnlyList<FileAssociationRow> _rows = [];
    private string _filter = string.Empty;
    private string? _message;
    private bool _messageIsError;
    private bool _isBusy;
    private bool _loaded;
    private IStringLocalizer<Strings>? _localizer;

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>Gets the rows, for tests.</summary>
    internal IReadOnlyList<FileAssociationRow> Rows => _rows;

    /// <summary>Gets the text of the last outcome, or <see langword="null"/>.</summary>
    internal string? Message => _message;

    private IEnumerable<FileAssociationRow> VisibleRows => string.IsNullOrWhiteSpace(_filter)
        ? _rows
        : _rows.Where(row => row.Target.Contains(_filter.Trim(), StringComparison.OrdinalIgnoreCase)
            || row.Candidates.Any(app => app.Name.Contains(_filter.Trim(), StringComparison.OrdinalIgnoreCase)));

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        await LoadAsync();
    }

    /// <summary>
    /// Builds one row per declared target and action (ADR 0121, section 4): extensions first, then
    /// media types, then URI schemes, each sorted. A configured default is kept only when it names a
    /// candidate, since the OS skips one that names an uninstalled app.
    /// </summary>
    /// <param name="apps">Installed apps.</param>
    /// <param name="defaults">Configured defaults, the user's first (<see cref="KazoApps.GetDefaultHandlersAsync"/>).</param>
    internal static IReadOnlyList<FileAssociationRow> BuildRows(IReadOnlyList<KazoAppInfo> apps, IReadOnlyList<KazoFileAssociation> defaults)
    {
        Dictionary<(string Target, string Action), SortedSet<KazoAppInfo>> declared = [];
        Comparer<KazoAppInfo> byName = Comparer<KazoAppInfo>.Create(static (a, b) =>
        {
            int name = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            return name != 0 ? name : StringComparer.Ordinal.Compare(a.Id, b.Id);
        });

        foreach (KazoAppInfo app in apps.Where(static app => app.Kind == AppKind.Window))
        {
            foreach (KazoAppFileHandler handler in app.FileHandlers)
            {
                IEnumerable<string> targets = handler.Extensions.Select(static e => e.ToLowerInvariant());
                if (handler.MediaType is { } mediaType)
                {
                    targets = targets.Append(mediaType.ToLowerInvariant());
                }

                foreach (string target in targets)
                {
                    foreach (string action in handler.Actions.Select(static a => a.ToLowerInvariant()))
                    {
                        if (!declared.TryGetValue((target, action), out SortedSet<KazoAppInfo>? set))
                        {
                            declared[(target, action)] = set = new SortedSet<KazoAppInfo>(byName);
                        }

                        set.Add(app);
                    }
                }
            }
        }

        return [.. declared
            .Select(pair =>
            {
                KazoAppInfo[] candidates = [.. pair.Value];
                return new FileAssociationRow(
                    pair.Key.Target,
                    pair.Key.Action,
                    candidates,
                    FindDefault(defaults, pair.Key, KazoFileAssociationLayer.User, candidates),
                    FindDefault(defaults, pair.Key, KazoFileAssociationLayer.System, candidates));
            })
            .OrderBy(static row => Group(row.Target))
            .ThenBy(static row => row.Target, StringComparer.Ordinal)
            .ThenBy(static row => row.Action == "open" ? 0 : 1)];
    }

    private static string? FindDefault(
        IReadOnlyList<KazoFileAssociation> defaults, (string Target, string Action) key, KazoFileAssociationLayer layer, KazoAppInfo[] candidates) =>
        defaults.FirstOrDefault(row => row.Layer == layer
                && string.Equals(row.Target, key.Target, StringComparison.Ordinal)
                && string.Equals(row.Action, key.Action, StringComparison.Ordinal)
                && candidates.Any(app => string.Equals(app.Id, row.AppId, StringComparison.Ordinal)))
            ?.AppId;

    private static int Group(string target) =>
        target.StartsWith('.') ? 0 : target.StartsWith(SchemePrefix, StringComparison.Ordinal) ? 2 : 1;

    private async Task LoadAsync()
    {
        try
        {
            IReadOnlyList<KazoAppInfo> apps = await KazoApps.GetInstalledAsync(Context.CancellationToken);
            IReadOnlyList<KazoFileAssociation> defaults = await KazoApps.GetDefaultHandlersAsync(Context.CancellationToken);
            _rows = BuildRows(apps, defaults);
        }
        catch (KazoUnauthorizedAccessException exception)
        {
            ShowError(L["FileAssociations.Error.Read", exception.Message]);
        }
        finally
        {
            _loaded = true;
        }
    }

    /// <summary>Writes the user's choice for <paramref name="row"/>; an empty value removes it.</summary>
    internal async Task ChooseAsync(FileAssociationRow row, string? appId)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        _message = null;
        try
        {
            bool reset = string.IsNullOrEmpty(appId);
            KazoDefaultHandlerStatus status = reset
                ? await KazoApps.ResetDefaultHandlerAsync(row.Target, row.Action, Context.CancellationToken)
                : await KazoApps.SetDefaultHandlerAsync(row.Target, appId!, row.Action, Context.CancellationToken);
            if (status == KazoDefaultHandlerStatus.Changed)
            {
                string? name = reset ? null : row.Candidates.FirstOrDefault(app => app.Id == appId)?.Name;
                _message = name is null ? L["FileAssociations.Saved.Reset", row.Label] : L["FileAssociations.Saved", row.Label, name];
                _messageIsError = false;
            }
            else
            {
                ShowError(L["FileAssociations.Error." + status, row.Label]);
            }

            await LoadAsync();
        }
        catch (KazoUnauthorizedAccessException exception)
        {
            ShowError(L["FileAssociations.Error.Write", exception.Message]);
        }
        finally
        {
            _isBusy = false;
            StateHasChanged();
        }
    }

    private string FallbackLabel(FileAssociationRow row) => row.FallbackAppId is { } fallback
        ? (row.SystemAppId is not null
            ? L["FileAssociations.SystemDefault", NameOf(row, fallback)]
            : L["FileAssociations.OnlyApp", NameOf(row, fallback)])
        : L["FileAssociations.AskEachTime"];

    private static string NameOf(FileAssociationRow row, string appId) =>
        row.Candidates.FirstOrDefault(app => app.Id == appId)?.Name ?? appId;

    private string ActionLabel(string action) => action == "edit" ? L["FileAssociations.Action.Edit"] : L["FileAssociations.Action.Open"];

    private void ShowError(string message)
    {
        _message = message;
        _messageIsError = true;
    }
}
