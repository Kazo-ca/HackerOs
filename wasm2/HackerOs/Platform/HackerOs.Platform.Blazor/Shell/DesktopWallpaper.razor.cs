using HackerOs.Platform.Core.Appearance;
using HackerOs.Theming.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HackerOs.Platform.Blazor.Shell;

/// <summary>
/// The wallpaper of one screen (ADR 0202 §4), given to <c>DesktopArea.WallpaperContent</c> by the
/// desktop shell for screen 1 and for every secondary screen, and placed behind the mobile home
/// screen by <c>MobileShell</c> with <see cref="Mobile"/> (ADR 0217 §2). It follows <see cref="WallpaperService"/>:
/// a change in Settings, a <c>nano ~/.config/hackeros/appearance.config</c> or a new session shows the
/// new image at once.
/// </summary>
public sealed partial class DesktopWallpaper : IAsyncDisposable
{
    private const string ModulePath = "./_content/HackerOs.Platform.Blazor/Shell/DesktopWallpaper.razor.js";

    private ElementReference _element;
    private WallpaperImage? _image;
    private WallpaperImage? _drawn;
    private IJSObjectReference? _module;
    private bool _disposed;

    [Inject]
    private WallpaperService Wallpapers { get; set; } = null!;

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    /// <summary>Gets or sets the number of the screen, 1 for the main tab.</summary>
    [Parameter]
    public int ScreenNumber { get; set; } = 1;

    /// <summary>
    /// Gets or sets whether this is the mobile home screen's wallpaper (<c>mobileWallpaper</c>,
    /// ADR 0217) rather than a desktop screen's; <see cref="ScreenNumber"/> is then ignored.
    /// </summary>
    [Parameter]
    public bool Mobile { get; set; }

    /// <summary>The value this surface shows: the mobile one, or the screen's own or screen 1's.</summary>
    private string CurrentValue => Mobile ? Wallpapers.Current.Mobile : Wallpapers.Current.ForScreen(ScreenNumber);

    private string CssClass => $"desktop-wallpaper fit-{WallpaperCatalog.FormatFit(_image?.Fit ?? WallpaperFit.Fill)}";

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        Wallpapers.Changed += OnWallpaperChanged;
        try
        {
            await Wallpapers.EnsureInitializedAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The settings could not be read: the theme's background stays.
            return;
        }

        await LoadAsync();
    }

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        if (_image is not null || CurrentValue.Length > 0)
        {
            await LoadAsync();
        }
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed || _image is null || ReferenceEquals(_image, _drawn))
        {
            return;
        }

        try
        {
            _module ??= await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
            await _module.InvokeVoidAsync("show", _element, _image.Content, _image.MediaType, _image.AssetPath);
            _drawn = _image;
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException)
        {
            // Prerendering, a closed screen or a closing page: nothing to draw on.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        Wallpapers.Changed -= OnWallpaperChanged;
        if (_module is null)
        {
            return;
        }

        try
        {
            if (_drawn is not null)
            {
                await _module.InvokeVoidAsync("release", _element);
            }

            await _module.DisposeAsync();
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException or TaskCanceledException or ObjectDisposedException)
        {
            // The page is closing: the browser frees the blob addresses with it.
        }
    }

    private void OnWallpaperChanged() => _ = InvokeAsync(async () =>
    {
        await LoadAsync();
        StateHasChanged();
    });

    private async Task LoadAsync()
    {
        WallpaperImage? image = Mobile ? await Wallpapers.GetMobileImageAsync() : await Wallpapers.GetImageAsync(ScreenNumber);
        if (image is null || _image is null || image.Value != _image.Value || image.Fit != _image.Fit || !ReferenceEquals(image.Content, _image.Content))
        {
            if (image is null && _drawn is not null && _module is not null)
            {
                // The element goes away with the image: free its blob address first.
                try
                {
                    await _module.InvokeVoidAsync("release", _element);
                }
                catch (Exception exception) when (exception is JSException or JSDisconnectedException or TaskCanceledException)
                {
                    // The page is closing.
                }
            }

            _image = image;
            _drawn = null;
        }
    }
}
