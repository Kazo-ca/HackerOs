using System.Globalization;
using System.Text;
using System.Text.Json;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Blazor.Interop;
using Kazo.Os.AppSdk.Clipboard;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.IO;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Diagnostic;

/// <summary>
/// IndexedDB Diagnostic: shows every object store of the OS database, resolves each filesystem
/// entry's virtual path, and exports the whole snapshot as JSON. It also lists the browser databases
/// of web sites (<c>hackweb.*</c>, ADR 0088) and can clear one.
/// </summary>
/// <remarks>
/// Uses only the public SDK (ADR 0055, ADR 0056 section 11):
/// <list type="bullet">
/// <item><see cref="HackStorageDiagnostics"/> (capability <c>system.storage.inspect</c>, dangerous:
/// the snapshot bypasses file permissions, so an administrator approves this app at its first
/// launch);</item>
/// <item>the save dialog and <see cref="HackFile"/> to save the JSON inside Kazo OS;</item>
/// <item><see cref="HackClipboard"/> (capability <c>clipboard.write</c>) to copy the JSON: the OS
/// clipboard mirrors its text to the real machine's clipboard (ADR 0065), so it can be pasted outside
/// Kazo OS;</item>
/// <item><see cref="BrowserFileDownloader"/> of the SDK to download the JSON to the real browser
/// (ADR 0060);</item>
/// <item><see cref="HackCrossOriginIsolation"/> (no capability) for the page's cross-origin
/// isolation (ADR 0131, section 5; ADR 0146).</item>
/// </list>
/// </remarks>
public partial class DiagnosticWindow : IAsyncDisposable
{
    private BrowserFileDownloader? _downloader;
    private HackStorageSnapshot? _snapshot;
    private string _selectedStoreName = string.Empty;
    private string _searchFilter = string.Empty;
    private bool _isLoading = true;
    private bool _showRawJson;
    private string? _toastMessage;
    private string? _loadError;
    private IReadOnlyList<HackWebSiteDatabaseInfo> _sites = [];
    private string? _sitesError;
    private string? _selectedSiteName;
    private bool _confirmDelete;
    private IStringLocalizer<Strings>? _localizer;
    private HackCrossOriginIsolationStatus? _isolation;

    /// <summary>The inspector's texts in the language of its own process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>The culture of the numbers shown (the process's locale).</summary>
    private CultureInfo Formats => AppContext.FormatCulture;

    private HackWebSiteDatabaseInfo? SelectedSite =>
        _selectedSiteName is null ? null : _sites.FirstOrDefault(site => site.Name == _selectedSiteName);

    private HackStorageStoreSnapshot? SelectedStore =>
        _snapshot?.Stores.FirstOrDefault(store => store.Name == _selectedStoreName);

    /// <inheritdoc />
    protected override async Task OnAppInitializedAsync()
    {
        _downloader = new BrowserFileDownloader(JS);
        await RefreshAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_downloader is not null)
        {
            await _downloader.DisposeAsync();
        }
    }

    private async Task RefreshAsync()
    {
        _isLoading = true;
        _loadError = null;
        StateHasChanged();

        try
        {
            _snapshot = await HackStorageDiagnostics.GetSnapshotAsync(AppContext, AppContext.CancellationToken);
            if (SelectedStore is null)
            {
                // Start on the inode table, the store the path column explains.
                _selectedStoreName = (_snapshot.Stores.FirstOrDefault(store => store.Kind == HackStorageStoreKind.FileEntries)
                    ?? _snapshot.Stores.FirstOrDefault())?.Name ?? string.Empty;
            }
        }
        catch (UnauthorizedAccessException)
        {
            _loadError = L["Error.NotGranted"];
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException or InvalidOperationException or Microsoft.JSInterop.JSException)
        {
            _loadError = L["Error.LoadFailed", exception.Message];
        }

        await LoadSitesAsync();
        await LoadIsolationAsync();
        _isLoading = false;
        StateHasChanged();
    }

    /// <summary>Lists the web sites' browser databases (ADR 0088); a failure leaves the OS stores shown.</summary>
    private async Task LoadSitesAsync()
    {
        _sitesError = null;
        try
        {
            _sites = await HackStorageDiagnostics.GetWebSiteDatabasesAsync(AppContext.CancellationToken);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or NotSupportedException or InvalidOperationException or Microsoft.JSInterop.JSException)
        {
            _sites = [];
            _sitesError = L["Error.SitesUnavailable", exception.Message];
        }
    }

    /// <summary>
    /// Reads the page's cross-origin isolation through the SDK (ADR 0131, section 5; ADR 0146), which
    /// needs no capability; a context that does not provide it shows no badge.
    /// </summary>
    private async Task LoadIsolationAsync()
    {
        try
        {
            _isolation = await HackCrossOriginIsolation.GetStatusAsync(AppContext.CancellationToken);
        }
        catch (NotSupportedException)
        {
            _isolation = null;
        }
    }

    /// <summary>The reason in the inspector's language.</summary>
    private string IsolationReason(HackCrossOriginIsolationReason reason) => reason switch
    {
        HackCrossOriginIsolationReason.Setting => L["Isolation.Reason.Setting"],
        HackCrossOriginIsolationReason.MissingHeaders => L["Isolation.Reason.MissingHeaders"],
        HackCrossOriginIsolationReason.InsecureContext => L["Isolation.Reason.InsecureContext"],
        HackCrossOriginIsolationReason.Browser => L["Isolation.Reason.Browser"],
        HackCrossOriginIsolationReason.NotBrowser => L["Isolation.Reason.NotBrowser"],
        _ => IsolationStatus.Format(reason)
    };

    /// <summary>The tooltip: every line of the status, as <c>cat /proc/hackeros/workers</c> prints it.</summary>
    private static string IsolationDetails(HackCrossOriginIsolationStatus isolation) => IsolationStatus.Describe(isolation);

    private void SelectStore(string storeName)
    {
        _selectedStoreName = storeName;
        _selectedSiteName = null;
        _confirmDelete = false;
        _searchFilter = string.Empty;
    }

    private void SelectSite(string databaseName)
    {
        _selectedSiteName = databaseName;
        _confirmDelete = false;
    }

    private async Task DeleteSiteAsync()
    {
        if (SelectedSite is not { } site)
        {
            return;
        }

        _confirmDelete = false;
        try
        {
            await HackStorageDiagnostics.DeleteWebSiteDatabaseAsync(site.Name, AppContext.CancellationToken);
            ShowToast(L["Toast.Deleted", site.Name]);
            _selectedSiteName = null;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or Microsoft.JSInterop.JSException)
        {
            ShowToast(L["Toast.DeleteFailed", exception.Message]);
        }

        await LoadSitesAsync();
        StateHasChanged();
    }

    /// <summary>Formats a size as the browser's storage settings do: B, KB, MB (o, Ko, Mo in French).</summary>
    private string FormatSize(long bytes) => bytes switch
    {
        < 1024 => L["Size.Bytes", bytes.ToString(Formats)],
        < 1024 * 1024 => L["Size.KB", (bytes / 1024.0).ToString("0.#", Formats)],
        _ => L["Size.MB", (bytes / (1024.0 * 1024)).ToString("0.#", Formats)],
    };

    private void ToggleRawJsonView() => _showRawJson = !_showRawJson;

    private async Task CopyToClipboardAsync()
    {
        if (_snapshot is null)
        {
            return;
        }

        try
        {
            await HackClipboard.SetTextAsync(_snapshot.ToExportJson(), AppContext.CancellationToken);
            ShowToast(L["Toast.Copied"]);
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowToast(L["Toast.CopyFailed", exception.Message]);
        }
    }

    private async Task DownloadJsonAsync()
    {
        if (_snapshot is null || _downloader is null)
        {
            return;
        }

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(_snapshot.ToExportJson()), writable: false);
        await _downloader.DownloadFileFromStreamAsync(DumpFileName(), stream);
        ShowToast(L["Toast.Downloaded"]);
    }

    private async Task ExportJsonFileAsync()
    {
        if (_snapshot is null)
        {
            return;
        }

        SaveFileDialogResult result = await SaveFileAsync(new SaveFileDialogRequest
        {
            Title = L["Dialog.ExportTitle"],
            InitialDirectory = Kazo.Os.App.Abstractions.VirtualPath.Parse(HackEnvironment.GetFolderPath(HackSpecialFolder.UserProfile)),
            SuggestedFileName = DumpFileName(),
            DefaultExtension = ".json",
            Filters = [new FileDialogFilter(L["Dialog.JsonFilter"], [".json"], ["application/json"])],
            ConfirmOverwrite = true
        });

        if (result.Status != FileDialogStatus.Selected || result.Resource is null)
        {
            return;
        }

        try
        {
            await HackFile.WriteAllTextAsync(result.Resource.Path.Value, _snapshot.ToExportJson(), AppContext.CancellationToken);
            ShowToast(L["Toast.Exported", result.Resource.Path.Value]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowToast(L["Toast.ExportFailed", exception.Message]);
        }
    }

    private static string DumpFileName() => $"indexeddb-dump-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";

    private void ShowToast(string message)
    {
        _toastMessage = message;
        StateHasChanged();
        _ = Task.Delay(3500).ContinueWith(_ =>
        {
            _toastMessage = null;
            InvokeAsync(StateHasChanged);
        }, TaskScheduler.Default);
    }

    private IEnumerable<(string Id, string Path, string Kind, string OwnerId, string? ContentHash)> FilteredFsEntries(HackStorageStoreSnapshot store)
    {
        if (store.Records.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement element in store.Records.EnumerateArray())
        {
            string id = element.TryGetProperty("id", out JsonElement i) ? i.GetString() ?? "" : "";
            string kind = element.TryGetProperty("kind", out JsonElement k) ? k.ToString() : "";
            string ownerId = element.TryGetProperty("ownerId", out JsonElement o) ? o.GetString() ?? "" : "";
            string? contentHash = element.TryGetProperty("contentHash", out JsonElement ch) ? ch.GetString() : null;
            string path = _snapshot?.ResolvedPaths.GetValueOrDefault(id) ?? "/";

            if (string.IsNullOrWhiteSpace(_searchFilter) ||
                path.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ||
                id.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ||
                ownerId.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
            {
                yield return (id, path, kind, ownerId, contentHash);
            }
        }
    }

    private IEnumerable<(string ParentId, string Name, string EntryId)> FilteredFsLinks(HackStorageStoreSnapshot store)
    {
        if (store.Records.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement element in store.Records.EnumerateArray())
        {
            string parentId = element.TryGetProperty("parentId", out JsonElement p) ? p.GetString() ?? "" : "";
            string name = element.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
            string entryId = element.TryGetProperty("entryId", out JsonElement e) ? e.GetString() ?? "" : "";

            if (string.IsNullOrWhiteSpace(_searchFilter) ||
                name.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ||
                parentId.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ||
                entryId.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
            {
                yield return (parentId, name, entryId);
            }
        }
    }

    /// <summary>Other stores are shown as JSON; with a filter, only the records whose JSON contains it.</summary>
    private string FilteredJson(HackStorageStoreSnapshot store)
    {
        if (string.IsNullOrWhiteSpace(_searchFilter) || store.Records.ValueKind != JsonValueKind.Array)
        {
            return store.FormattedJson;
        }

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (JsonElement record in store.Records.EnumerateArray()
                .Where(record => record.GetRawText().Contains(_searchFilter, StringComparison.OrdinalIgnoreCase)))
            {
                record.WriteTo(writer);
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
