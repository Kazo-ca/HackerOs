using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Settings;

/// <summary>
/// Settings &gt; Apps &gt; Sources and block list (ADR 0189 §1, §5 and §6): the catalogs and the
/// repositories of <c>/etc/hackeros/app-sources.config</c> with their trust level, and the block list
/// of <c>/etc/hackeros/app-policy.config</c>; an administrator adds or removes entries here.
/// </summary>
/// <remarks>
/// Uses only the public SDK, <see cref="KazoAppPackages"/> (capability <c>apps.manage</c>). Each
/// change makes the OS ask an administrator to authenticate in its own window (or nothing under a
/// root process); this panel never sees a password. The same files can be edited with
/// <c>sudo nano</c> or <c>hackpkg sources</c> / <c>hackpkg policy</c>.
/// </remarks>
public partial class AppSourcesPanel
{
    private KazoPackageSources? _sources;
    private KazoPackagePolicy? _policy;
    private string? _newSource;
    private bool _newSourceTrusted;
    private KazoPackageBlockKind _newBlockKind = KazoPackageBlockKind.App;
    private string? _newBlockValue;
    private bool _isBusy;
    private string? _message;
    private IStringLocalizer<Strings>? _localizer;

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>The three kinds of rule, in the order of the file.</summary>
    private IEnumerable<(KazoPackageBlockKind Kind, IReadOnlyList<string> Values)> BlockGroups =>
        _policy is null
            ? []
            :
            [
                (KazoPackageBlockKind.App, _policy.BlockedApps),
                (KazoPackageBlockKind.Category, _policy.BlockedCategories),
                (KazoPackageBlockKind.Capability, _policy.BlockedCapabilities)
            ];

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        try
        {
            _sources = await KazoAppPackages.GetSourcesAsync();
            _policy = await KazoAppPackages.GetPolicyAsync();
        }
        catch (NotSupportedException)
        {
            _message = L["Sources.Error.Unsupported"];
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or AppGatewayAccessDeniedException)
        {
            _message = L["Apps.Error.NotAllowed"];
        }
    }

    /// <summary>The repository entries of a list: catalogs are shown in their own list.</summary>
    private static IEnumerable<string> Repositories(IReadOnlyList<string> keys) =>
        keys.Where(static key => !key.StartsWith(CatalogPrefix, StringComparison.Ordinal));

    private const string CatalogPrefix = "catalog:";

    private static string CatalogKey(string name) => CatalogPrefix + name;

    private static string TrustClass(KazoPackageTrust trust) => trust switch
    {
        KazoPackageTrust.Verified => "is-verified",
        KazoPackageTrust.Trusted => "is-trusted",
        _ => "is-untrusted"
    };

    private string TrustText(KazoPackageTrust trust) => trust switch
    {
        KazoPackageTrust.Verified => L["Sources.Trust.Verified"],
        KazoPackageTrust.Trusted => L["Sources.Trust.Trusted"],
        _ => L["Sources.Trust.Untrusted"]
    };

    private string KindText(KazoPackageBlockKind kind) => kind switch
    {
        KazoPackageBlockKind.App => L["Policy.Apps"],
        KazoPackageBlockKind.Category => L["Policy.Categories"],
        _ => L["Policy.Capabilities"]
    };

    private Task AddSourceAsync()
    {
        string source = (_newSource ?? string.Empty).Trim();
        bool trusted = _newSourceTrusted;
        return RunAsync(async () =>
        {
            _sources = await KazoAppPackages.AddSourceAsync(source, trusted);
            _newSource = null;
            _newSourceTrusted = false;
        });
    }

    private Task RemoveSourceAsync(string source) =>
        RunAsync(async () => _sources = await KazoAppPackages.RemoveSourceAsync(source));

    private Task BlockAsync()
    {
        string value = (_newBlockValue ?? string.Empty).Trim();
        return RunAsync(async () =>
        {
            _policy = await KazoAppPackages.SetPolicyAsync(_newBlockKind, value, blocked: true);
            _newBlockValue = null;
        });
    }

    private Task SetBlockedAsync(KazoPackageBlockKind kind, string value, bool blocked) =>
        RunAsync(async () => _policy = await KazoAppPackages.SetPolicyAsync(kind, value, blocked));

    /// <summary>Runs one change and turns its failure into a message.</summary>
    private async Task RunAsync(Func<Task> change)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        _message = null;
        StateHasChanged();
        try
        {
            await change();
        }
        catch (KazoPackageException exception)
        {
            _message = exception.Message;
        }
        catch (AppGatewayAccessDeniedException)
        {
            _message = L["Apps.Error.NotAllowed"];
        }
        catch (UnauthorizedAccessException)
        {
            _message = L["Apps.Error.NoAdmin"];
        }
        catch (NotSupportedException)
        {
            _message = L["Sources.Error.Unsupported"];
        }
        catch (IOException exception)
        {
            _message = L["Apps.Error.CannotWrite", exception.Message];
        }
        finally
        {
            _isBusy = false;
            StateHasChanged();
        }
    }
}
