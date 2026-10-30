using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Administration;
using HackerOs.AppSdk.Globalization;
using HackerOs.AppSdk.IO;
using HackerOs.AppSdk.Settings;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Theming.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace HackerOs.Apps.Settings;

/// <summary>
/// Settings &gt; Appearance &gt; Wallpaper (ADR 0202 §5): the image of the desktop, for every screen
/// or for one secondary screen, and how it covers the screen. Writes the <c>wallpaper</c>,
/// <c>wallpaperScreen&lt;n&gt;</c> and <c>wallpaperFit</c> keys of <c>appearance.config</c> through
/// <see cref="HackSettings.OpenSystemAsync(IAppExecutionContext, string, CancellationToken)"/>; the
/// shell follows the file and shows the image at once.
/// </summary>
/// <remarks>
/// The choices are the theme's background, the built-in wallpapers, the images of
/// <c>/usr/share/hackeros/wallpapers</c>, of <c>~/.local/share/hackeros/wallpapers</c> and of the
/// installed theme packs, and any PNG, JPEG or WebP file picked with « Browse… », whose path is kept
/// as it is (ADR 0202 §2).
/// </remarks>
public sealed partial class WallpaperPanel : IAsyncDisposable
{
    /// <summary>Key of the wallpaper of screen 1.</summary>
    internal const string WallpaperKey = "wallpaper";

    /// <summary>Key of how the wallpaper covers the screens.</summary>
    internal const string FitKey = "wallpaperFit";

    /// <summary>Highest screen number that can have its own wallpaper.</summary>
    internal const int MaxScreenNumber = 9;

    /// <summary>Most library images listed, so a huge folder does not load every file.</summary>
    internal const int MaxLibraryImages = 60;

    private const string ModulePath = "./_content/HackerOs.Apps.Settings/WallpaperPanel.razor.js";

    private readonly Dictionary<string, ElementReference> _thumbnails = new(StringComparer.Ordinal);
    private readonly HashSet<string> _drawnThumbnails = new(StringComparer.Ordinal);
    private HackSettingsDocument? _document;
    private IReadOnlyList<LibraryImage> _library = [];
    private IReadOnlyList<int> _screens = [1];
    private int _screen = 1;
    private string _fit = WallpaperCatalog.FormatFit(WallpaperFit.Fill);
    private string? _message;
    private bool _messageIsError;
    private bool _isBusy;
    private IJSObjectReference? _module;
    private IStringLocalizer<Strings>? _localizer;

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    /// <summary>Gets or sets the layer the choices are written to: the user's file or the system default.</summary>
    [Parameter]
    public SettingLayer TargetLayer { get; set; } = SettingLayer.User;

    /// <summary>
    /// Gets or sets the « Browse… » action: shows the file-open dialog and returns the chosen path, or
    /// <see langword="null"/> when cancelled. The Settings window gives it (the dialog belongs to a window).
    /// </summary>
    [Parameter]
    public Func<Task<string?>>? BrowseImage { get; set; }

    /// <summary>Gets the key written for the selected screen.</summary>
    internal string SelectedKey => _screen == 1 ? WallpaperKey : ScreenKey(_screen);

    /// <summary>Gets the value in effect for the selected screen's key.</summary>
    internal string SelectedValue => _document?.Get<string>(SelectedKey) ?? string.Empty;

    /// <summary>Gets the library images found.</summary>
    internal IReadOnlyList<LibraryImage> Library => _library;

    /// <summary>Gets the screens offered, screen 1 first.</summary>
    internal IReadOnlyList<int> Screens => _screens;

    /// <summary>Gets the text of the last outcome, or <see langword="null"/>.</summary>
    internal string? Message => _message;

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= Context.GetLocalizer<Strings>();

    private bool IsDisabled => _document is null || _isBusy || IsBlocked(SelectedKey);

    /// <summary>Gets the key of a secondary screen's own wallpaper.</summary>
    /// <param name="number">Screen number, 2 to <see cref="MaxScreenNumber"/>.</param>
    internal static string ScreenKey(int number) => $"wallpaperScreen{number}";

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        try
        {
            _document = await HackSettings.OpenSystemAsync(Context, SettingsWindow.AppearanceDocumentId, Context.CancellationToken);
            _document.Changed += OnDocumentChanged;
            _fit = _document.Get<string>(FitKey);
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowMessage(L["Appearance.Error.CannotRead", exception.Message], isError: true);
            return;
        }

        _screens = ReadScreens();
        await LoadLibraryAsync();
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        LibraryImage[] pending = [.. _library.Where(image => !_drawnThumbnails.Contains(image.Path) && _thumbnails.ContainsKey(image.Path))];
        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            _module ??= await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
            foreach (LibraryImage image in pending)
            {
                _drawnThumbnails.Add(image.Path);
                await _module.InvokeVoidAsync("showThumbnail", _thumbnails[image.Path], image.Content, image.MediaType);
            }
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException or NotSupportedException or TaskCanceledException)
        {
            // No JavaScript (headless renderer): the thumbnails stay empty, the names are shown.
        }
    }

    /// <summary>Selects which screen the next choice is for.</summary>
    /// <param name="number">Screen number.</param>
    internal void SelectScreen(int number)
    {
        _screen = number;
        _message = null;
    }

    /// <summary>Writes a wallpaper value for the selected screen.</summary>
    /// <param name="value">Empty, <c>builtin:&lt;id&gt;</c> or an absolute path.</param>
    internal Task ChooseAsync(string value) => WriteAsync(SelectedKey, value);

    /// <summary>« Browse… »: picks any PNG, JPEG or WebP file and chooses it.</summary>
    internal async Task BrowseAsync()
    {
        if (BrowseImage is null)
        {
            return;
        }

        string? path = await BrowseImage();
        if (path is null)
        {
            return;
        }

        if (!WallpaperCatalog.HasSupportedExtension(path))
        {
            ShowMessage(L["Wallpaper.Error.Format", path], isError: true);
            return;
        }

        await ChooseAsync(path);
        await LoadLibraryAsync();
        StateHasChanged();
    }

    /// <summary>Writes <c>wallpaperFit</c>.</summary>
    /// <param name="fit">One of <see cref="WallpaperCatalog.FitValues"/>.</param>
    internal Task ChangeFitAsync(string fit) => WriteAsync(FitKey, fit);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_document is not null)
        {
            _document.Changed -= OnDocumentChanged;
            await _document.DisposeAsync();
        }

        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("releaseAll", _thumbnails.Values.ToArray());
                await _module.DisposeAsync();
            }
            catch (Exception exception) when (exception is JSException or JSDisconnectedException or TaskCanceledException or ObjectDisposedException)
            {
                // The window is closing: the browser frees the blob addresses with it.
            }
        }
    }

    private Task OnFitChangedAsync(ChangeEventArgs args) => ChangeFitAsync(args.Value?.ToString() ?? string.Empty);

    private async Task WriteAsync(string key, string value)
    {
        if (_document is null || _isBusy)
        {
            return;
        }

        _isBusy = true;
        _message = null;
        try
        {
            // /etc/hackeros belongs to root: a system write uses this app's own rights, so a denial
            // raises the administrator approval of ADR 0042.
            await _document.SetAsync(key, value, TargetLayer, CancellationToken.None);
            if (key == FitKey)
            {
                _fit = value;
            }

            ShowMessage(TargetLayer == SettingLayer.System ? L["Appearance.SavedAsSystemDefault"] : L["Wallpaper.Saved"], isError: false);
        }
        catch (HackSettingLockedException exception)
        {
            ShowMessage(exception.Message, isError: true);
        }
        catch (UnauthorizedAccessException)
        {
            ShowMessage(L["Appearance.Error.AdminRequired"], isError: true);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
        {
            ShowMessage(L["Appearance.Error.CannotSave", exception.Message], isError: true);
        }
        finally
        {
            _isBusy = false;
        }

        StateHasChanged();
    }

    /// <summary>An external edit or another window changed the file.</summary>
    private void OnDocumentChanged(object? sender, HackSettingsChangedEventArgs args) =>
        _ = InvokeAsync(() =>
        {
            if (_document is not null && !_isBusy)
            {
                _fit = _document.Get<string>(FitKey);
            }

            StateHasChanged();
        });

    /// <summary>Screen 1, the open screens and every screen that already has its own wallpaper.</summary>
    private IReadOnlyList<int> ReadScreens()
    {
        SortedSet<int> numbers = [1];
        for (int number = 2; number <= MaxScreenNumber; number++)
        {
            if (!string.IsNullOrEmpty(_document?.Get<string>(ScreenKey(number))))
            {
                numbers.Add(number);
            }
        }

        try
        {
            if (HackScreens.IsSupportedFor(Context))
            {
                foreach (HackScreenInfo screen in HackScreens.GetState(Context).Screens.Where(static screen => screen.Number <= MaxScreenNumber))
                {
                    numbers.Add(screen.Number);
                }
            }
        }
        catch (Exception exception) when (exception is NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            // No screens on this host: screen 1 only.
        }

        return [.. numbers];
    }

    /// <summary>
    /// Lists the images of the wallpaper folders and of the installed theme packs, plus the files
    /// chosen with « Browse… » when they are elsewhere, and reads each one for its thumbnail.
    /// </summary>
    private async Task LoadLibraryAsync()
    {
        using IDisposable scope = HackExecutionContext.Enter(Context);
        string home = HackUser.Describe(Context).HomeDirectory;
        List<(string Path, string Origin)> candidates = [];
        await AddFilesAsync(candidates, WallpaperCatalog.SystemDirectory, "system");
        await AddFilesAsync(candidates, WallpaperCatalog.GetUserDirectory(home), "user");
        foreach (string packs in new[] { WallpaperCatalog.SystemThemePacksDirectory, WallpaperCatalog.GetUserThemePacksDirectory(home) })
        {
            foreach (string pack in await ListAsync(packs, files: false))
            {
                await AddFilesAsync(candidates, $"{pack}/{WallpaperCatalog.PackWallpapersFolder}", "pack");
            }
        }

        for (int number = 1; number <= MaxScreenNumber; number++)
        {
            string value = _document?.Get<string>(number == 1 ? WallpaperKey : ScreenKey(number)) ?? string.Empty;
            if (value.StartsWith('/') && candidates.All(candidate => candidate.Path != value))
            {
                candidates.Add((value, "file"));
            }
        }

        List<LibraryImage> images = [];
        foreach ((string path, string origin) in candidates.Take(MaxLibraryImages))
        {
            LibraryImage? image = _library.FirstOrDefault(known => known.Path == path) ?? await ReadImageAsync(path, origin);
            if (image is not null)
            {
                images.Add(image);
            }
        }

        _library = images;
    }

    private static async Task AddFilesAsync(List<(string Path, string Origin)> candidates, string directory, string origin)
    {
        foreach (string path in (await ListAsync(directory, files: true)).Where(WallpaperCatalog.HasSupportedExtension).Order(StringComparer.Ordinal))
        {
            candidates.Add((path, origin));
        }
    }

    /// <summary>Lists a folder's files or sub-folders; a missing or unreadable folder lists nothing.</summary>
    private static async Task<string[]> ListAsync(string directory, bool files)
    {
        try
        {
            return files
                ? await HackDirectory.GetFilesAsync(directory)
                : await HackDirectory.GetDirectoriesAsync(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>Reads an image for its thumbnail; a file too large, unreadable or not an image is skipped.</summary>
    private static async Task<LibraryImage?> ReadImageAsync(string path, string origin)
    {
        try
        {
            byte[] content = await HackFile.ReadAllBytesAsync(path);
            return content.LongLength <= WallpaperCatalog.MaxImageBytes && WallpaperCatalog.TryDetectMediaType(content, out string mediaType)
                ? new LibraryImage(path, HackPath.GetFileName(path), origin, content, mediaType)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void ShowMessage(string message, bool isError)
    {
        _message = message;
        _messageIsError = isError;
        StateHasChanged();
    }

    private SettingLayer SourceOf(string key) => _document?.GetSource(key) ?? SettingLayer.Default;

    private bool IsLocked(string key) => _document?.IsLocked(key) == true;

    /// <summary>A locked key cannot be changed for oneself; the system default stays editable.</summary>
    private bool IsBlocked(string key) => TargetLayer == SettingLayer.User && IsLocked(key);

    private string OptionClass(string value) => value == SelectedValue ? "wallpaper-option is-selected" : "wallpaper-option";

    private string Checked(string value) => value == SelectedValue ? "true" : "false";

    private string ScreenClass(int number) => number == _screen ? "wallpaper-screen is-selected" : "wallpaper-screen";

    /// <summary>An image of the wallpaper library, with its bytes for the thumbnail.</summary>
    /// <param name="Path">Absolute path.</param>
    /// <param name="Name">File name.</param>
    /// <param name="Origin"><c>system</c>, <c>user</c>, <c>pack</c> or <c>file</c> (chosen with « Browse… »).</param>
    /// <param name="Content">The file's bytes.</param>
    /// <param name="MediaType">Media type read from the bytes.</param>
    internal sealed record LibraryImage(string Path, string Name, string Origin, byte[] Content, string MediaType);
}
