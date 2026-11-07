using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Appearance;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Kazo.Os.Platform.Blazor.Shell;

/// <summary>
/// Draws an app's icon on a shell surface (Start menu, mobile home and drawer) and redraws it when the
/// icon pack changes (ADR 0203 §4).
/// </summary>
public sealed partial class ShellAppIcon : IDisposable
{
    private IconPackService? _iconPacks;

    /// <summary>The app whose icon is drawn.</summary>
    [Parameter, EditorRequired]
    public AppManifest App { get; set; } = null!;

    /// <summary>Width and height in CSS pixels.</summary>
    [Parameter]
    public int Size { get; set; } = 20;

    /// <summary>
    /// Kept for compatibility and ignored: since ADR 0232 §2 every surface shows the app's declared image
    /// before the category icon.
    /// </summary>
    [Parameter]
    [Obsolete("Ignored since ADR 0232: every surface shows the app's declared image.")]
    public bool UseDeclaredImage { get; set; }

    /// <summary>Whether an image fills its parent tile instead of keeping <see cref="Size"/>.</summary>
    [Parameter]
    public bool Fill { get; set; }

    [Inject]
    private IServiceProvider Services { get; set; } = null!;

    /// <summary>
    /// The image to draw, or <see langword="null"/> for the Lucide icon (ADR 0232 §2): the pack's image of
    /// the app, else the app's declared image, else the pack's image of its category.
    /// </summary>
    private string? ImageUrl
    {
        get
        {
            string? declared = App.Presentation.IconAssetPaths.FirstOrDefault();
            return _iconPacks?.FindAppImage(App.Id, App.Presentation.Category, declared is not null)
                ?? (declared is null ? null : LazyLoading.StaticWebAssetAppAssetSource.RelativeUrl(App, declared));
        }
    }

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        // Optional so a host or a test without the service still draws the built-in icons.
        _iconPacks = Services.GetService<IconPackService>();
        if (_iconPacks is not null)
        {
            _iconPacks.Changed += OnIconPackChanged;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_iconPacks is not null)
        {
            _iconPacks.Changed -= OnIconPackChanged;
        }
    }

    private void OnIconPackChanged() => _ = InvokeAsync(StateHasChanged);
}
