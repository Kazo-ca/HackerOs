using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Files;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.Net.Hosting;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.WebServer.Manager;

/// <summary>
/// Web Server Manager (ADR 0077, section 3): the IIS-Manager-like window of the hosted static sites.
/// It lists the system sites, the user's sites and the external sites of web apps; it creates a site
/// from a folder, edits its bindings and default documents, starts and stops it, opens it in the
/// browser and shows its logs. Every change is a write of a <c>.site</c> file, which
/// <c>kazowebd</c> applies at once; the service itself is driven like <c>systemctl</c>.
/// </summary>
/// <remarks>
/// Launched with a folder path as first argument (File Explorer's "Publish as a web site…"), it opens
/// the form of a new site rooted there.
/// </remarks>
public partial class WebServerManagerWindow
{
    private IStringLocalizer<Strings>? _localizer;

    /// <summary>The Manager's texts in its own process's language (ADR 0104); site files, logs and paths are not translated.</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    private readonly CancellationTokenSource _lifetime = new();
    private WebServerSnapshot? _snapshot;
    private WebSiteRow? _selected;
    private SiteEditor? _editor;
    private DetailTab _tab = DetailTab.Settings;
    private List<string> _errors = [];
    private string? _warning;
    private string? _message;
    private bool _messageIsError;
    private IReadOnlyList<string> _accessLines = [];
    private IReadOnlyList<string> _errorLines = [];

    private enum DetailTab
    {
        Settings,
        Logs
    }

    private IEnumerable<(string Title, string Hint, IReadOnlyList<WebSiteRow> Rows)> Groups
    {
        get
        {
            IReadOnlyList<WebSiteRow> rows = _snapshot?.Rows ?? [];
            yield return (L["Group.System"], "/etc/kazoweb/sites", [.. rows.Where(static row => row.Declaration?.Scope == KazoWebSiteScope.System)]);
            yield return (L["Group.User"], "~/.config/kazoweb/sites", [.. rows.Where(static row => row.Declaration?.Scope == KazoWebSiteScope.User)]);
            yield return (L["Group.External"], L["Group.External.Hint"], [.. rows.Where(static row => row.State == WebSiteState.External)]);
        }
    }

    private string DaemonText => _snapshot?.Daemon switch
    {
        null => L["Daemon.NotInstalled"],
        { ProcessId: { } pid } => L["Daemon.Running", pid],
        _ => L["Daemon.Stopped"]
    };

    private string DaemonTitle => _snapshot?.Daemon?.StartType is { } mode ? L["Daemon.StartType", mode] : "systemctl status kazowebd";

    private string DaemonClass => _snapshot?.Daemon?.ProcessId is null ? "wsm-daemon-down" : "wsm-daemon-up";

    private string? SelectedUrl => _selected?.MainUrl;

    private string StatusText
    {
        get
        {
            if (_snapshot is null)
            {
                return L["Status.Loading"];
            }

            int declared = _snapshot.Rows.Count(static row => row.Declaration is not null);
            int running = _snapshot.Rows.Count(static row => row.State == WebSiteState.Running);
            int external = _snapshot.Rows.Count(static row => row.State == WebSiteState.External);
            return L["Status.Summary", Count("Status.Hosted", declared), Count("Status.Running", running), Count("Status.External", external), Count("Status.Sockets", _snapshot.Listeners.Count)];
        }
    }

    /// <inheritdoc />
    protected override async Task OnAppInitializedAsync()
    {
        await RefreshAsync();
        if (PublishedFolder(LaunchIntent) is { } published)
        {
            await StartNewAsync(published);
        }
        else if (LaunchFile is { } folder)
        {
            await StartNewAsync(folder.Value);
        }
    }

    /// <summary>"Publish as a web site" chosen again while the window is open (single instance): a new site for that folder.</summary>
    /// <param name="intent">The intent the OS handed.</param>
    protected override async Task OnIntentReceivedAsync(AppIntentDelivery intent)
    {
        if (PublishedFolder(intent) is { } folder)
        {
            await StartNewAsync(folder);
            StateHasChanged();
        }
    }

    /// <summary>The folder of a "Publish as a web site" intent (ADR 0151 §8), or <see langword="null"/>.</summary>
    internal static string? PublishedFolder(AppIntentDelivery? intent) =>
        intent is { IntentId: WebServerManagerApp.PublishSiteIntent }
            ? KazoFileMenuIntent.ReadPaths(intent.PayloadJson).FirstOrDefault()
            : null;

    /// <summary>Reads everything again, keeping the selection.</summary>
    private async Task RefreshAsync()
    {
        try
        {
            _snapshot = await WebSiteStore.LoadAsync(_lifetime.Token);
            if (_selected is { } selected)
            {
                _selected = _snapshot.Rows.FirstOrDefault(row => row.Name == selected.Name && row.Declaration?.Path == selected.Declaration?.Path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowMessage(L["Message.ReadFailed", exception.Message], error: true);
        }
    }

    private bool IsSelected(WebSiteRow row) =>
        _selected is { } selected && selected.Name == row.Name && selected.Declaration?.Path == row.Declaration?.Path;

    private async Task SelectAsync(WebSiteRow row)
    {
        _selected = row;
        _tab = DetailTab.Settings;
        _message = null;
        _editor = row.Declaration is { } declaration ? SiteEditor.From(declaration) : null;
        await ValidateAsync();
    }

    private Task NewSiteAsync() => StartNewAsync(KazoUser.Current.HomeDirectory + "/www");

    private async Task StartNewAsync(string folder)
    {
        _snapshot ??= await WebSiteStore.LoadAsync(_lifetime.Token);
        KazoWebSite suggested = WebSiteStore.Suggest(folder, _snapshot);
        _selected = null;
        _tab = DetailTab.Settings;
        _editor = SiteEditor.FromNew(suggested);
        ShowMessage(L["Message.NewSite", folder], error: false);
        await ValidateAsync();
    }

    private void CancelNew()
    {
        _editor = null;
        _message = null;
    }

    private async Task SetScopeAsync(KazoWebSiteScope scope)
    {
        if (_editor is null)
        {
            return;
        }

        _editor.Scope = scope;
        await ValidateAsync();
    }

    private void Validate()
    {
        if (_editor is null)
        {
            _errors = [];
            return;
        }

        _errors = [.. KazoWebSiteFile.Validate(_editor.ToSite(), _editor.Scope, AppContext)];
    }

    private async Task ValidateAsync()
    {
        Validate();
        _warning = _editor is { Scope: KazoWebSiteScope.System } editor
            ? await WebSiteStore.CheckWwwDataAsync(editor.Root, _lifetime.Token)
            : null;
    }

    private async Task BrowseRootAsync()
    {
        if (_editor is null)
        {
            return;
        }

        SelectFolderDialogResult result = await SelectFolderAsync(new SelectFolderDialogRequest
        {
            Title = L["Dialog.RootFolder"],
            InitialDirectory = TryParse(_editor.Root),
            AllowCreateFolder = true
        });
        if (result.Status == FileDialogStatus.Selected && result.Resource is { } folder)
        {
            _editor.Root = folder.Path.Value;
            await ValidateAsync();
        }
    }

    private async Task SaveAsync()
    {
        if (_editor is not { } editor)
        {
            return;
        }

        await ValidateAsync();
        if (_errors.Count > 0)
        {
            return;
        }

        try
        {
            string path = await WebSiteStore.SaveAsync(editor.ToSite(), editor.Scope, editor.OriginalPath, _lifetime.Token);
            ShowMessage(L["Message.Saved", path], error: false);
            await RefreshAsync();
            _selected = _snapshot?.Rows.FirstOrDefault(row => row.Declaration?.Path == path);
            _editor = _selected?.Declaration is { } declaration ? SiteEditor.From(declaration) : null;
        }
        catch (UnauthorizedAccessException)
        {
            ShowMessage(editor.Scope == KazoWebSiteScope.System
                ? L["Message.SaveNeedsAdmin"]
                : L["Message.SaveDenied"], error: true);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            ShowMessage(L["Message.SaveFailed", exception.Message], error: true);
        }
    }

    private async Task ToggleEnabledAsync()
    {
        if (_editor is not { } editor)
        {
            return;
        }

        editor.Enabled = !editor.WasEnabled;
        await SaveAsync();
    }

    private async Task DeleteAsync()
    {
        if (_selected?.Declaration is not { } declaration)
        {
            return;
        }

        MessageBoxDialogResult answer = await MessageBox(
            L["Dialog.Delete.Title"],
            L["Dialog.Delete.Message", declaration.Path, declaration.Name],
            MessageBoxType.YesNo);
        if (answer.Result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await WebSiteStore.DeleteAsync(declaration, _lifetime.Token);
            ShowMessage(L["Message.Deleted", declaration.Path], error: false);
            _selected = null;
            _editor = null;
            await RefreshAsync();
        }
        catch (UnauthorizedAccessException)
        {
            ShowMessage(L["Message.DeleteNeedsAdmin"], error: true);
        }
        catch (IOException exception)
        {
            ShowMessage(L["Message.DeleteFailed", exception.Message], error: true);
        }
    }

    private async Task OpenInBrowserAsync()
    {
        if (SelectedUrl is not { } url)
        {
            return;
        }

        try
        {
            await KazoApps.LaunchAsync("org.kazoos.browser", [url], _lifetime.Token);
        }
        catch (Exception exception) when (exception is KazoAppLaunchException or UnauthorizedAccessException)
        {
            ShowMessage(L["Message.BrowserFailed", exception.Message], error: true);
        }
    }

    private async Task ShowLogsAsync()
    {
        if (_editor is null)
        {
            return;
        }

        _tab = DetailTab.Logs;
        (_accessLines, _errorLines) = await WebSiteStore.ReadLogsAsync(_editor.OriginalName ?? _editor.Name, cancellationToken: _lifetime.Token);
    }

    private Task StartDaemonAsync() => ControlDaemonAsync(static (service, token) => service.StartAsync(token), "Message.Daemon.Started");

    private Task StopDaemonAsync() => ControlDaemonAsync(static (service, token) => service.StopAsync(token), "Message.Daemon.Stopped");

    private Task RestartDaemonAsync() => ControlDaemonAsync(static async (service, token) =>
    {
        await service.StopAsync(token);
        await service.StartAsync(token);
    }, "Message.Daemon.Restarted");

    private async Task ControlDaemonAsync(Func<AppSdk.ServiceProcess.KazoServiceController, CancellationToken, Task> action, string doneKey)
    {
        if (_snapshot?.Daemon is not { } daemon)
        {
            return;
        }

        try
        {
            await action(daemon, _lifetime.Token);
            ShowMessage(L[doneKey], error: false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            ShowMessage(L["Message.Daemon.Failed", exception.Message], error: true);
        }

        await RefreshAsync();
    }

    /// <summary>A counted text of the status bar, singular or plural by the process's language.</summary>
    private string Count(string key, int count) => Strings.Count(L, key, count);

    private void ShowMessage(string message, bool error)
    {
        _message = message;
        _messageIsError = error;
    }

    private string StateLabel(WebSiteState state) => state switch
    {
        WebSiteState.Running => L["State.Running"],
        WebSiteState.Partial => L["State.Partial"],
        WebSiteState.Down => L["State.Down"],
        WebSiteState.Stopped => L["State.Stopped"],
        WebSiteState.Invalid => L["State.Invalid"],
        _ => L["State.External"]
    };

    private static VirtualPath? TryParse(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? null : VirtualPath.Parse(KazoPath.GetDirectoryName(path) ?? "/");
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Cancels the reads still running when the window closes.</summary>
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The form's fields, as text, before they become a <see cref="KazoWebSite"/>.</summary>
    private sealed class SiteEditor
    {
        public string Name { get; set; } = string.Empty;

        public string Root { get; set; } = string.Empty;

        public string Bindings { get; set; } = string.Empty;

        public string DefaultDocuments { get; set; } = string.Empty;

        public bool DirectoryBrowsing { get; set; }

        public bool Enabled { get; set; } = true;

        public bool WasEnabled { get; private init; }

        public KazoWebSiteScope Scope { get; set; } = KazoWebSiteScope.User;

        public string? OriginalPath { get; private init; }

        public string? OriginalName { get; private init; }

        public static SiteEditor From(KazoWebSiteDeclaration declaration)
        {
            KazoWebSite site = declaration.Site ?? new KazoWebSite { Name = declaration.Name };
            return new SiteEditor
            {
                Name = site.Name,
                Root = site.Root,
                Bindings = string.Join('\n', site.Bindings),
                DefaultDocuments = string.Join(", ", site.DefaultDocuments),
                DirectoryBrowsing = site.DirectoryBrowsing,
                Enabled = site.Enabled,
                WasEnabled = site.Enabled,
                Scope = declaration.Scope,
                OriginalPath = declaration.Path,
                OriginalName = declaration.Name
            };
        }

        public static SiteEditor FromNew(KazoWebSite site) => new()
        {
            Name = site.Name,
            Root = site.Root,
            Bindings = string.Join('\n', site.Bindings),
            DefaultDocuments = string.Join(", ", site.DefaultDocuments),
            DirectoryBrowsing = site.DirectoryBrowsing,
            Enabled = true,
            Scope = KazoWebSiteScope.User
        };

        public KazoWebSite ToSite() => new()
        {
            Name = Name.Trim(),
            Root = Root.Trim(),
            Bindings = [.. Bindings.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)],
            DefaultDocuments = [.. DefaultDocuments.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)],
            DirectoryBrowsing = DirectoryBrowsing,
            Enabled = Enabled
        };
    }
}
