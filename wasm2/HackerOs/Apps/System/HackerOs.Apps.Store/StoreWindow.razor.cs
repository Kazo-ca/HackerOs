using System.Globalization;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Apps;
using HackerOs.AppSdk.Blazor;
using HackerOs.AppSdk.Globalization;
using HackerOs.Simulation.Abstractions.Gateways;
using Microsoft.Extensions.Localization;

namespace HackerOs.Apps.Store;

/// <summary>The page the Store shows.</summary>
internal enum StoreView
{
    /// <summary>Categories, or the search results.</summary>
    Home,

    /// <summary>The « Installed » tab, with the updates.</summary>
    Installed,

    /// <summary>One app's page.</summary>
    Detail
}

/// <summary>
/// The Hack Store window (ADR 0188 §1 to §3, ADR 0212): home by categories, search, an app's page,
/// Install for me / for everyone, Open, Update, Uninstall, the « Installed » tab and « Restart to
/// finish ». The same component is the desktop window and the mobile screen.
/// </summary>
/// <remarks>
/// <para>
/// Only the public SDK: <see cref="HackAppPackages"/> reads the catalogs (downloaded by the OS
/// itself), installs and removes; the OS asks an administrator in its own windows when the install is
/// for every user, from an untrusted source or of a blocked app (ADR 0189 §4). The Store never sees
/// a password.
/// </para>
/// <para>
/// Launch arguments: <c>--page=installed</c> opens the « Installed » tab (the update notification
/// does), <c>--app=&lt;appId&gt;</c> opens an app's page.
/// </para>
/// </remarks>
public partial class StoreWindow
{
    private const string PageArgument = "--page=";
    private const string AppArgument = "--app=";

    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private StoreCatalog _store = StoreCatalog.Empty;
    private StoreView _view = StoreView.Home;
    private StoreView _returnView = StoreView.Home;
    private string? _query;
    private string? _selectedAppId;
    private string? _categoryId;
    private bool _isLoading = true;
    private string? _busyAppId;
    private bool _isBusy;
    private string? _message;
    private bool _messageIsError;
    private string? _description;
    private string? _descriptionKey;
    private bool _isDescriptionLoading;
    private IStringLocalizer<Strings>? _localizer;

    /// <summary>The window's texts in the language of the Store process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>The language of the Store process, for the catalogs' texts.</summary>
    private CultureInfo Culture => L is HackStringLocalizer hack ? hack.Culture : HackLocale.DefaultLanguage;

    private StoreEntry? Selected => _selectedAppId is null ? null : _store.Find(_selectedAppId);

    private IReadOnlyList<StoreEntry> SearchResults => _store.Search(_query, Culture);

    private bool IsSearching => !string.IsNullOrWhiteSpace(_query);

    /// <inheritdoc />
    protected override async Task OnAppInitializedAsync()
    {
        foreach (string argument in LaunchArguments)
        {
            if (argument.StartsWith(PageArgument, StringComparison.Ordinal) && argument[PageArgument.Length..] == "installed")
            {
                _view = StoreView.Installed;
            }
            else if (argument.StartsWith(AppArgument, StringComparison.Ordinal))
            {
                _selectedAppId = argument[AppArgument.Length..];
                _view = StoreView.Detail;
            }
        }

        await LoadAsync(refresh: false);
        if (_view == StoreView.Detail && Selected is { } entry)
        {
            await LoadDescriptionAsync(entry);
        }
        else if (_view == StoreView.Detail)
        {
            _view = StoreView.Home;
        }
    }

    /// <summary>Reads the catalogs, what is installed and the updates.</summary>
    private async Task LoadAsync(bool refresh)
    {
        _isLoading = true;
        try
        {
            IReadOnlyList<HackAppCatalog> catalogs = await HackAppPackages.GetCatalogsAsync(refresh);
            IReadOnlyList<HackInstalledPackage> installed = await HackAppPackages.GetInstalledAsync();
            IReadOnlyList<HackPackageUpdate> updates = await HackAppPackages.GetUpdatesAsync();
            _store = new StoreCatalog(catalogs, installed, updates, _removed);
        }
        catch (NotSupportedException)
        {
            ShowError(L["Error.Unsupported"]);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or AppGatewayAccessDeniedException)
        {
            ShowError(L["Error.NotAllowed"]);
        }
        catch (Exception exception) when (exception is HackPackageException or IOException)
        {
            ShowError(L["Error.Generic", exception.Message]);
        }
        finally
        {
            _isLoading = false;
        }
    }

    private Task RefreshAsync()
    {
        _message = null;
        return LoadAsync(refresh: true);
    }

    private void ShowHome()
    {
        _view = StoreView.Home;
        _categoryId = null;
        _selectedAppId = null;
    }

    private void ShowInstalled()
    {
        _view = StoreView.Installed;
        _selectedAppId = null;
    }

    private void ShowCategory(string categoryId)
    {
        _view = StoreView.Home;
        _query = null;
        _categoryId = categoryId;
    }

    private void OnQueryChanged(Microsoft.AspNetCore.Components.ChangeEventArgs args)
    {
        _query = args.Value?.ToString();
        if (_view == StoreView.Detail || IsSearching)
        {
            _view = StoreView.Home;
            _categoryId = null;
        }
    }

    private async Task OpenDetailAsync(StoreEntry entry)
    {
        _returnView = _view == StoreView.Detail ? _returnView : _view;
        _selectedAppId = entry.AppId;
        _view = StoreView.Detail;
        await LoadDescriptionAsync(entry);
    }

    private void CloseDetail()
    {
        _view = _returnView;
        _selectedAppId = null;
    }

    private async Task LoadDescriptionAsync(StoreEntry entry)
    {
        if (entry.App is not { } app)
        {
            _description = null;
            return;
        }

        string key = $"{app.Source}@{app.LatestVersion}:{Culture.TwoLetterISOLanguageName}";
        if (key == _descriptionKey)
        {
            return;
        }

        _descriptionKey = key;
        _description = null;
        _isDescriptionLoading = true;
        try
        {
            _description = await HackAppPackages.GetCatalogDescriptionAsync(app, language: Culture.TwoLetterISOLanguageName);
        }
        catch (Exception exception) when (exception is HackPackageException or IOException or NotSupportedException)
        {
            // The page still shows the short description and the facts.
            _description = null;
        }
        finally
        {
            _isDescriptionLoading = false;
        }
    }

    private Task InstallAsync(StoreEntry entry, HackPackageScope scope) =>
        RunAsync(entry, async () =>
        {
            HackPackageInstallReport report = await HackAppPackages.InstallAsync(entry.App!.Source, scope);
            _removed.Remove(entry.AppId);
            ShowInfo(L[report.SystemWide ? "Message.InstalledForEveryone" : "Message.InstalledForMe", report.Name, report.Version]);
        });

    private Task UpdateAsync(StoreEntry entry) =>
        RunAsync(entry, async () =>
        {
            HackPackageUpdate update = entry.Update!;
            HackPackageInstallReport report = await HackAppPackages.InstallAsync(
                $"{update.Source}@{update.AvailableVersion}",
                update.SystemWide ? HackPackageScope.System : HackPackageScope.User);
            ShowInfo(L["Message.Updated", report.Name, report.Version]);
        });

    private async Task UpdateAllAsync()
    {
        foreach (StoreEntry entry in _store.Updatable)
        {
            await UpdateAsync(entry);
            if (_messageIsError)
            {
                return;
            }
        }
    }

    private Task RemoveAsync(StoreEntry entry) =>
        RunAsync(entry, async () =>
        {
            HackInstalledPackage removed = await HackAppPackages.RemoveAsync(
                entry.AppId,
                entry.Installed?.SystemWide == true ? HackPackageScope.System : HackPackageScope.User);
            // Hot-add (ADR 0214 §1): only an app loaded in the page waits for the restart.
            if (removed.Loaded)
            {
                _removed.Add(removed.AppId);
            }

            ShowInfo(L[removed.Loaded ? "Message.RemovedRestart" : "Message.Removed", entry.Name]);
        });

    private Task RollbackAsync(StoreEntry entry) =>
        RunAsync(entry, async () =>
        {
            HackPackageInstallReport report = await HackAppPackages.RollbackAsync(
                entry.AppId,
                entry.Installed?.SystemWide == true ? HackPackageScope.System : HackPackageScope.User);
            ShowInfo(L[report.RestartRequired ? "Message.RolledBackRestart" : "Message.RolledBack", report.Name, report.Version]);
        });

    private Task OpenAsync(StoreEntry entry) =>
        RunAsync(entry, async () => await HackApps.LaunchAsync(entry.AppId), reload: false);

    private async Task RestartAsync()
    {
        _isBusy = true;
        try
        {
            await HackAppPackages.RestartToFinishAsync();
        }
        catch (HackPackageException exception) when (exception.Code == "apps.package.nothing-pending")
        {
            ShowInfo(L["Message.NothingPending"]);
        }
        catch (Exception exception) when (exception is HackPackageException or NotSupportedException or UnauthorizedAccessException or AppGatewayAccessDeniedException)
        {
            ShowError(L["Error.Generic", exception.Message]);
        }
        finally
        {
            _isBusy = false;
        }
    }

    /// <summary>Runs one action on an app, then reads the Store again so every button follows.</summary>
    private async Task RunAsync(StoreEntry entry, Func<Task> action, bool reload = true)
    {
        _busyAppId = entry.AppId;
        _isBusy = true;
        _message = null;
        try
        {
            await action();
            if (reload)
            {
                await LoadAsync(refresh: false);
            }
        }
        catch (HackPackageException exception)
        {
            ShowError(DescribePackageError(exception));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or AppGatewayAccessDeniedException)
        {
            ShowError(L["Error.NotApproved"]);
        }
        catch (Exception exception) when (exception is HackAppLaunchException or NotSupportedException or IOException)
        {
            ShowError(L["Error.Generic", exception.Message]);
        }
        finally
        {
            _busyAppId = null;
            _isBusy = false;
        }
    }

    private string DescribePackageError(HackPackageException exception) => exception.Code switch
    {
        "apps.package.blocked" => L["Error.Blocked"],
        "apps.package.installed-for-system" => L["Error.InstalledForSystem"],
        "apps.package.sdk-incompatible" => L["Error.Incompatible", exception.Message],
        "apps.package.missing-api" => L["Error.Incompatible", exception.Message],
        "apps.package.download-failed" => L["Error.Offline"],
        "apps.package.no-previous" => L["Error.NoPrevious"],
        "apps.package.reinstall-required" => L["Error.ReinstallRequired"],
        "apps.package.unsigned" => L["Error.Unsigned"],
        "apps.package.bad-signature" => L["Error.BadSignature"],
        _ => L["Error.Generic", exception.Message]
    };

    private void ShowInfo(string message)
    {
        _message = message;
        _messageIsError = false;
    }

    private void ShowError(string message)
    {
        _message = message;
        _messageIsError = true;
    }

    private string CategoryLabel(string categoryId) =>
        categoryId == StoreCatalog.OtherCategoryId
            ? L["Category.Other"]
            : _store.Categories.FirstOrDefault(category => category.Id == categoryId)?.GetLabel(Culture) ?? categoryId;

    private string TrustText(HackPackageTrust trust) => trust switch
    {
        HackPackageTrust.Verified => L["Trust.Verified"],
        HackPackageTrust.Trusted => L["Trust.Trusted"],
        _ => L["Trust.Untrusted"]
    };

    private static string TrustClass(HackPackageTrust trust) => trust switch
    {
        HackPackageTrust.Verified => "is-verified",
        HackPackageTrust.Trusted => "is-trusted",
        _ => "is-untrusted"
    };

    private static string TrustIcon(HackPackageTrust trust) => trust switch
    {
        HackPackageTrust.Verified => "shield-check",
        HackPackageTrust.Trusted => "shield",
        _ => "shield-alert"
    };

    private string StateText(StoreEntry entry) => entry.State switch
    {
        StoreAppState.Installed => entry.Installed!.SystemWide ? L["State.InstalledForEveryone"] : L["State.InstalledForMe"],
        StoreAppState.UpdateAvailable => L["State.UpdateAvailable", entry.Update!.AvailableVersion],
        StoreAppState.PendingRestart => L["State.PendingRestart"],
        _ => string.Empty
    };

    private string SizeText(long bytes) => StoreCatalog.FormatSize(bytes, Culture);
}
