using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Blazor;
using HackerOs.AppSdk.Globalization;
using HackerOs.AppSdk.IO;
using HackerOs.AppSdk.Settings;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Theming.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace HackerOs.Apps.ThemeEditor;

/// <summary>
/// The Theme Editor window (ADR 0103 §2), ported from <c>src/apps/theme-editor.ts</c> onto the
/// public SDK: token groups with typed inputs and help, live preview, generated documentation, and
/// themes saved as <c>.theme</c> files in <c>~/.config/hackeros/themes/</c>. The free CSS of the
/// legacy editor is not ported (ADR 0103 §2.4).
/// </summary>
public partial class ThemeEditorWindow : WindowAppBase, IWindowCloseGuard
{
    // Replaced at initialization by a document reading the window's texts (ADR 0104).
    private ThemeEditorDocument _document = new();
    private IStringLocalizer<Strings>? _localizer;
    private UserThemeLibraryResult _library = UserThemeLibraryResult.Empty;
    private string _newBaseThemeId = WellKnownThemeIds.HackerOs;
    private ThemePlatform _previewPlatform = ThemePlatform.Desktop;
    private string? _message;
    private bool _messageIsError;
    private bool _busy;

    /// <summary>The Theme Editor's texts in its own process's language (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>
    /// Gets the window title (ADR 0117 §4): <c>name — Theme Editor</c>, prefixed with <c>*</c> while
    /// unsaved. The window manager shows it; the toolbar repeats it.
    /// </summary>
    internal string DocumentTitle =>
        $"{(_document.IsDirty ? "*" : string.Empty)}{L["Title.Format", DisplayName]}";

    /// <summary>The theme's name, or "Untitled" in the app's language while it has none.</summary>
    private string DisplayName => string.IsNullOrWhiteSpace(_document.Name) ? L["Title.Untitled"] : _document.Name;

    private string HomeDirectory => HackUser.Describe(AppContext).HomeDirectory;

    private string SavePathHint => ThemeFile.IsValidId(_document.Id)
        ? L["Sidebar.SavesTo", UserThemeLibrary.GetUserThemePath(HomeDirectory, _document.Id)]
        : L["Sidebar.NotSaved"];

    /// <inheritdoc />
    protected override async Task OnAppInitializedAsync()
    {
        _document = new ThemeEditorDocument(L);
        await SetWindowTitleAsync(DocumentTitle);
        await RefreshLibraryAsync();

        // A .theme file opened from the File Explorer, "Open with" or `theme-editor <file>`.
        if (LaunchFile is { } launchFile)
        {
            await LoadAsync(launchFile, AppContext.FileSystem);
        }
    }

    /// <inheritdoc />
    /// <remarks>Opening, editing and saving all end in a render: the window title follows them here.</remarks>
    protected override Task OnAppAfterRenderAsync(bool firstRender) => SetWindowTitleAsync(DocumentTitle).AsTask();

    /// <summary>Re-reads the installed themes when the window comes back to the front (a theme copied meanwhile shows up).</summary>
    protected override Task OnWindowActivatedAsync() => RefreshLibraryAsync();

    /// <summary>Asks before closing with unsaved changes (ADR 0006).</summary>
    /// <param name="cancellationToken">Cancels the close request.</param>
    public async ValueTask<bool> ConfirmCloseAsync(CancellationToken cancellationToken = default)
    {
        if (!_document.IsDirty)
        {
            return true;
        }

        MessageBoxDialogResult answer = await MessageBox(
            L["Dialog.Unsaved.Title"], L["Dialog.Unsaved.Close", _document.Name], MessageBoxType.YesNo, cancellationToken);
        return answer.Result == MessageBoxResult.Yes;
    }

    // ──────────────────────────── Editing ────────────────────────────

    private void OnNameChanged(ChangeEventArgs args) => _document.Name = args.Value as string ?? string.Empty;

    private void OnIdChanged(ChangeEventArgs args) => _document.SetId(args.Value as string);

    private void OnBaseChanged(ChangeEventArgs args)
    {
        if (args.Value is string baseId && ThemeCatalog.TryGet(baseId, out ThemeDefinition? theme))
        {
            _document.BaseThemeId = theme.Id;
            _previewPlatform = theme.Platform;
        }
    }

    private void OnDesktopChanged(ChangeEventArgs args) => _document.ForDesktop = args.Value is true;

    private void OnMobileChanged(ChangeEventArgs args) => _document.ForMobile = args.Value is true;

    private void OnTokenChanged(string tokenName, string? value) => _document.SetValue(tokenName, value);

    // ──────────────────────────── Commands ────────────────────────────

    private async Task NewAsync()
    {
        if (!await ConfirmDiscardAsync())
        {
            return;
        }

        _document.Reset(_newBaseThemeId);
        _previewPlatform = ThemeCatalog.Get(_newBaseThemeId).Platform;
        _message = null;
    }

    private async Task OpenAsync()
    {
        if (!await ConfirmDiscardAsync())
        {
            return;
        }

        OpenFileDialogResult result = await OpenFileAsync(new OpenFileDialogRequest
        {
            Title = L["Dialog.Open.Title"],
            InitialDirectory = VirtualPath.Parse(HomeDirectory),
            Filters = [new FileDialogFilter("HackerOS themes", [ThemeFile.Extension], [ThemeFile.MediaType])],
            RequestedAccess = SelectedFileAccess.Read
        });
        if (result.Status == FileDialogStatus.Selected && result.Resources.Count > 0)
        {
            SelectedFileResource selected = result.Resources[0];
            await LoadAsync(selected.Path, AppContext.FileSystem.WithSelectedHandle(selected.Handle));
        }
    }

    private async Task OpenInstalledAsync(UserTheme theme)
    {
        if (theme.SourcePath is null || !await ConfirmDiscardAsync())
        {
            return;
        }

        await LoadAsync(VirtualPath.Parse(theme.SourcePath), AppContext.FileSystem);
    }

    /// <summary>Saves to <c>~/.config/hackeros/themes/&lt;id&gt;.theme</c>, where the shell and Settings find it.</summary>
    private async Task SaveAsync()
    {
        if (!CanSave())
        {
            return;
        }

        UserTheme theme = _document.ToUserTheme();
        if (_library.Themes.FirstOrDefault(other => other.Id == theme.Id) is { } existing
            && existing.SourcePath != UserThemeLibrary.GetUserThemePath(HomeDirectory, theme.Id))
        {
            Show(L["Message.IdInUse", theme.Id, existing.SourcePath ?? string.Empty], isError: true);
            return;
        }

        await RunAsync(async () =>
        {
            string directory = UserThemeLibrary.GetUserDirectory(HomeDirectory);
            await HackDirectory.CreateDirectoryAsync(directory, AppContext.CancellationToken);
            string path = UserThemeLibrary.GetUserThemePath(HomeDirectory, theme.Id);
            string? error = await ThemeFileIo.WriteAsync(AppContext.FileSystem, VirtualPath.Parse(path), ThemeFile.Serialize(theme), AppContext.CancellationToken);
            if (error is not null)
            {
                Show(L["Message.SaveFailed", path, error], isError: true);
                return;
            }

            _document.MarkSaved(path);
            Show(L["Message.Saved", path], isError: false);
            await RefreshLibraryAsync();
        });
    }

    /// <summary>Writes a copy of the theme anywhere (to share it): the file is the export.</summary>
    private async Task ExportAsync()
    {
        if (!CanSave())
        {
            return;
        }

        SaveFileDialogResult result = await SaveFileAsync(new SaveFileDialogRequest
        {
            Title = L["Dialog.Export.Title"],
            InitialDirectory = VirtualPath.Parse(HomeDirectory),
            SuggestedFileName = $"{_document.Id}{ThemeFile.Extension}",
            DefaultExtension = ThemeFile.Extension,
            Filters = [new FileDialogFilter("HackerOS themes", [ThemeFile.Extension], [ThemeFile.MediaType])],
            ConfirmOverwrite = true
        });
        if (result.Status != FileDialogStatus.Selected || result.Resource is null)
        {
            return;
        }

        VirtualPath target = result.Resource.Path;
        IAppFileSystemGateway fileSystem = AppContext.FileSystem.WithSelectedHandle(result.Resource.Handle);
        await RunAsync(async () =>
        {
            string? error = await ThemeFileIo.WriteAsync(fileSystem, target, ThemeFile.Serialize(_document.ToUserTheme()), AppContext.CancellationToken);
            Show(error is null ? L["Message.Exported", target.Value] : L["Message.ExportFailed", target.Value, error], error is not null);
        });
    }

    /// <summary>Saves, then selects the theme in the user's <c>appearance.config</c>; the shell applies it at once.</summary>
    private async Task UseAsync(ThemePlatform platform)
    {
        if (_document.IsDirty || _document.Path is null)
        {
            await SaveAsync();
            if (_document.IsDirty || _document.Path is null)
            {
                return;
            }
        }

        await RunAsync(async () =>
        {
            await using HackSettingsDocument appearance = await HackSettings.OpenSystemAsync(AppContext, "appearance", AppContext.CancellationToken);
            string key = platform == ThemePlatform.Mobile ? "mobileThemeId" : "desktopThemeId";
            await appearance.SetAsync(key, _document.Id, SettingLayer.User, AppContext.CancellationToken);
            Show(L[platform == ThemePlatform.Mobile ? "Message.NowMobile" : "Message.NowDesktop", _document.Name], isError: false);
        });
    }

    // ──────────────────────────── Helpers ────────────────────────────

    private async Task LoadAsync(VirtualPath path, IAppFileSystemGateway fileSystem)
    {
        (string? content, string? error) = await ThemeFileIo.ReadAsync(fileSystem, path, AppContext.CancellationToken);
        if (content is null)
        {
            Show(L["Message.OpenFailed", path.Value, error ?? L["Error.Unknown"]], isError: true);
            return;
        }

        ThemeFileParseResult parsed = ThemeFile.Parse(content, ThemeOrigin.User, path.Value);
        if (parsed.Theme is null)
        {
            Show(L["Message.OpenFailed", path.Value, string.Join(" ", parsed.Diagnostics.Where(static d => d.IsError).Select(d => d.GetMessage(Strings.CultureOf(L))))], isError: true);
            return;
        }

        // A file outside ~/.config/hackeros/themes (or in /etc) is edited as a new user theme:
        // Save writes the user's own copy, as a desktop does with a system theme.
        string userPath = UserThemeLibrary.GetUserThemePath(HomeDirectory, parsed.Theme.Id);
        _document.Load(parsed.Theme, path.Value == userPath ? path.Value : null);
        _previewPlatform = parsed.Theme.Platforms[0];
        int warnings = parsed.Diagnostics.Count(static d => !d.IsError);
        Show(warnings == 0
                ? L["Message.Opened", path.Value]
                : L["Message.OpenedWithWarnings", path.Value, warnings, string.Join(" ", parsed.Diagnostics.Select(d => d.ToString(Strings.CultureOf(L))))],
            isError: warnings > 0);
    }

    private async Task RefreshLibraryAsync()
    {
        try
        {
            _library = await UserThemeLibrary.LoadAsync(
                async (directory, token) => await HackDirectory.GetFilesAsync(directory, "*" + ThemeFile.Extension, cancellationToken: token),
                async (directory, token) => await HackDirectory.GetDirectoriesAsync(directory, cancellationToken: token),
                (path, token) => HackFile.ReadAllTextAsync(path, token),
                HomeDirectory,
                AppContext.CancellationToken);
        }
        catch (Exception exception) when (exception is HackIOException or HackUnauthorizedAccessException)
        {
            _library = UserThemeLibraryResult.Empty;
        }
    }

    private bool CanSave()
    {
        IReadOnlyList<string> problems = _document.GetProblems();
        if (problems.Count == 0)
        {
            return true;
        }

        Show(string.Join(" ", problems), isError: true);
        return false;
    }

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!_document.IsDirty)
        {
            return true;
        }

        MessageBoxDialogResult answer = await MessageBox(
            L["Dialog.Unsaved.Title"], L["Dialog.Unsaved.Discard", _document.Name], MessageBoxType.YesNo);
        return answer.Result == MessageBoxResult.Yes;
    }

    private async Task RunAsync(Func<Task> action)
    {
        _busy = true;
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is HackIOException or HackUnauthorizedAccessException or UnauthorizedAccessException or IOException)
        {
            Show(exception.Message, isError: true);
        }
        finally
        {
            _busy = false;
        }
    }

    private void Show(string message, bool isError)
    {
        _message = message;
        _messageIsError = isError;
    }

    private string GroupLabel(ThemeTokenGroup group) => Strings.GroupLabel(L, group);

    private string GroupIntro(ThemeTokenGroup group)
    {
        LocalizedString intro = L[$"Group.{group}.Intro"];
        return intro.ResourceNotFound ? string.Empty : intro.Value;
    }

    /// <summary>"desktop" or "mobile" in the app's language, after a base theme's name.</summary>
    private string PlatformText(ThemePlatform platform) => L[platform == ThemePlatform.Mobile ? "Platform.Mobile" : "Platform.Desktop"];
}
