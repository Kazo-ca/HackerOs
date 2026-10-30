using HackerOs.AppSdk;
using HackerOs.AppSdk.Globalization;
using HackerOs.AppSdk.IO;
using HackerOs.AppSdk.Settings;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Theming.Abstractions.IconPacks;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace HackerOs.Apps.Settings;

/// <summary>
/// Settings &gt; Appearance &gt; Icon pack (ADR 0203 §5): lists the installed icon packs with
/// <see cref="IconPackLibrary"/> through the public file API, and writes the chosen id to the OS
/// document <c>icons</c> (<c>~/.config/hackeros/icons.config</c>, or <c>/etc/hackeros/icons.config</c>
/// for the system default, which asks an administrator).
/// </summary>
public sealed partial class IconPackPanel : IAsyncDisposable
{
    /// <summary>How many images of a pack its button shows.</summary>
    internal const int PreviewSize = 4;

    private HackSettingsDocument? _document;
    private IReadOnlyList<IconPack> _packs = [];
    private string _selectedId = IconPackLibrary.BuiltInId;
    private int _warningCount;
    private bool _loaded;
    private bool _isSaving;
    private string? _message;
    private IStringLocalizer<Strings>? _localizer;

    /// <summary>The layer written: the user's own, or the system default (the window's layer picker).</summary>
    [Parameter]
    public SettingLayer TargetLayer { get; set; } = SettingLayer.User;

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= Context.GetLocalizer<Strings>();

    private bool IsDisabled =>
        _document is null || _isSaving
        || (TargetLayer == SettingLayer.User && _document.IsLocked(IconPackLibrary.PackIdKey));

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        try
        {
            _document = await HackSettings.OpenSystemAsync(Context, IconPackLibrary.SettingsDocumentId, Context.CancellationToken);
            _document.Changed += OnDocumentChanged;
            _selectedId = _document.Get<string>(IconPackLibrary.PackIdKey);
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _message = L["IconPack.Error.CannotRead", exception.Message];
        }

        await RefreshPacksAsync();
        _loaded = true;
    }

    /// <summary>Re-reads the installed packs (a pack copied with <c>cp</c> while Settings was open).</summary>
    internal async Task RefreshPacksAsync()
    {
        try
        {
            using IDisposable scope = HackExecutionContext.Enter(Context);
            IconPackFileAccess files = new(
                async (directory, token) => await HackDirectory.GetDirectoriesAsync(directory, cancellationToken: token),
                async (directory, token) => await HackDirectory.GetFilesAsync(directory, cancellationToken: token),
                HackFile.ReadAllBytesAsync);
            IconPackListResult list = await IconPackLibrary.ListAsync(files, HackUser.Describe(Context).HomeDirectory, Context.CancellationToken);
            List<IconPack> packs = [];
            int warnings = list.Diagnostics.Count;
            foreach (IconPackInfo info in list.Packs)
            {
                IconPackLoadResult loaded = await IconPackLibrary.LoadAsync(info, files, Context.CancellationToken);
                warnings += loaded.Diagnostics.Count;
                packs.Add(loaded.Pack);
            }

            _packs = packs;
            _warningCount = warnings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _packs = [];
            _message = L["IconPack.Error.CannotRead", exception.Message];
        }
    }

    /// <summary>A few images of a pack: its folder, its plain file, then its first app images.</summary>
    /// <param name="pack">The pack.</param>
    internal static IEnumerable<string> PreviewOf(IconPack pack)
    {
        IEnumerable<string?> preferred =
        [
            pack.FindFile(isDirectory: true, null),
            pack.FindFile(isDirectory: false, ".txt"),
            pack.FindApp("org.hackeros.settings", "system")
        ];
        IEnumerable<string> others = pack.Images
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => pair.Value);
        return preferred.OfType<string>().Concat(others).Distinct(StringComparer.Ordinal).Take(PreviewSize);
    }

    /// <summary>Writes <paramref name="packId"/> to the chosen layer.</summary>
    /// <param name="packId">A pack id, or <see cref="IconPackLibrary.BuiltInId"/>.</param>
    internal async Task SelectAsync(string packId)
    {
        if (_document is null || _isSaving)
        {
            return;
        }

        string previous = _selectedId;
        _selectedId = packId;
        _isSaving = true;
        _message = null;
        try
        {
            // /etc/hackeros belongs to root: a system write raises the administrator approval (ADR 0042).
            await _document.SetAsync(IconPackLibrary.PackIdKey, packId, TargetLayer, CancellationToken.None);
            _message = L["IconPack.Saved"];
        }
        catch (HackSettingLockedException exception)
        {
            _selectedId = previous;
            _message = exception.Message;
        }
        catch (UnauthorizedAccessException)
        {
            _selectedId = previous;
            _message = L["Appearance.Error.AdminRequired"];
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
        {
            _selectedId = previous;
            _message = L["Appearance.Error.CannotSave", exception.Message];
        }
        finally
        {
            _isSaving = false;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_document is not null)
        {
            _document.Changed -= OnDocumentChanged;
            await _document.DisposeAsync();
        }
    }

    private bool IsSelected(string packId) => string.Equals(_selectedId, packId, StringComparison.Ordinal);

    private string OptionClass(string packId) => IsSelected(packId) ? "icon-pack-option is-selected" : "icon-pack-option";

    /// <summary>An edit of <c>icons.config</c> from elsewhere (<c>nano</c>, another window).</summary>
    private void OnDocumentChanged(object? sender, HackSettingsChangedEventArgs args) =>
        _ = InvokeAsync(() =>
        {
            if (_document is not null && !_isSaving)
            {
                _selectedId = _document.Get<string>(IconPackLibrary.PackIdKey);
            }

            StateHasChanged();
        });
}
