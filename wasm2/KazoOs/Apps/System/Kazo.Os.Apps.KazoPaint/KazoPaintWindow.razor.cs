using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Accessibility;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Kazo.Os.Apps.KazoPaint;

/// <summary>
/// Hack Paint's window: an RGBA canvas editor that opens and saves real image files of the OS
/// (ADR 0103 §1). Files are read and written in C# through the app's gateway (<see cref="PaintFileStore"/>);
/// the collocated JS module only decodes and encodes pixels with the browser's codecs.
/// </summary>
public partial class KazoPaintWindow : WindowAppBase, IAsyncDisposable, IWindowCloseGuard
{
    private readonly PaintCanvasState _state = new();
    private ElementReference _root;
    private ElementReference _canvas;
    private IJSObjectReference? _module;
    private bool _drawing;
    private int _lastX;
    private int _lastY;
    private (int X, int Y)? _cropStart;
    private bool _panning;
    private bool _launchFileHandled;

    // Current document file. The gateway carries the dialog handle when the file lies outside the
    // app's home-folder capability, so later saves to the same path stay authorized.
    private VirtualPath? _path;
    private IAppFileSystemGateway? _fileSystem;
    private string? _error;
    private string? _busyMessage;
    private KazoAccessibilityPreferences _accessibility = KazoAccessibilityPreferences.Default;
    private int _jpegQuality = 92;
    private IStringLocalizer<Strings>? _localizer;

    /// <summary>Hack Paint's texts in its own process's language (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>The "Images" filter of the Open dialog: every format Hack Paint decodes.</summary>
    private FileDialogFilter ImageFilter => new(
        L["Filter.Images"], [.. ImageFileFormat.OpenExtensions], ["image/png", "image/jpeg", "image/bmp", "image/gif", "image/webp"]);

    /// <summary>The name shown for a document that has no file yet.</summary>
    private string Untitled => L["Title.Untitled"];

    [Inject]
    private IJSRuntime JsRuntime { get; set; } = default!;

    /// <summary>
    /// Gets the window title (ADR 0103 §1, ADR 0117 §4): <c>name.png — Hack Paint</c>, prefixed with
    /// <c>*</c> while unsaved. Shown by the window manager, not inside the app.
    /// </summary>
    internal string DocumentTitle => PaintTitle.Format(_path?.Value, _state.IsDirty, Untitled);

    private bool IsBusy => _busyMessage is not null;

    /// <summary>Gets or sets the JPEG quality, in percent, used when saving a <c>.jpg</c> file.</summary>
    private int JpegQuality
    {
        get => _jpegQuality;
        set => _jpegQuality = Math.Clamp(value, 10, 100);
    }

    /// <inheritdoc />
    /// <remarks>Names the window before it is first shown (ADR 0117 §2).</remarks>
    protected override Task OnAppInitializedAsync() => SetWindowTitleAsync(DocumentTitle).AsTask();

    /// <inheritdoc />
    protected override async Task OnAppAfterRenderAsync(bool firstRender)
    {
        // Every change of file or of saved state ends in a render: the title follows it here.
        await SetWindowTitleAsync(DocumentTitle);
        if (firstRender)
        {
            _module = await JsRuntime.InvokeAsync<IJSObjectReference>("import", "./_content/Kazo.Os.Apps.KazoPaint/KazoPaintWindow.razor.js");
            await _module.InvokeVoidAsync("guardShortcuts", _root);
            // ADR 0105: the picture keeps its own colours; the frame and grid drawn around it follow
            // the high-contrast preference, the busy marker the reduced-motion one.
            AppContext.Accessibility.Changed += OnAccessibilityChanged;
            if (await ReadAccessibilityAsync())
            {
                StateHasChanged();
            }
        }

        if (!_launchFileHandled)
        {
            // "Open with", a double-click in the File Explorer or `hack-paint <file>` passes the path
            // as the first argument (ADR 0050); decoding needs the module, hence after the first render.
            _launchFileHandled = true;
            if (LaunchFile is { } launchFile)
            {
                await LoadAsync(launchFile, AppContext.FileSystem);
                StateHasChanged();
                return;
            }
        }

        await RenderCanvasAsync();
    }

    /// <summary>Asks before closing a window whose image is not saved (ADR 0006).</summary>
    /// <param name="cancellationToken">Cancels the close request.</param>
    public async ValueTask<bool> ConfirmCloseAsync(CancellationToken cancellationToken = default)
    {
        if (!_state.IsDirty)
        {
            return true;
        }

        MessageBoxDialogResult answer = await MessageBox(
            L["Dialog.Unsaved.Title"],
            L["Dialog.Unsaved.Close", PaintTitle.FileName(_path?.Value, Untitled)],
            MessageBoxType.YesNo,
            cancellationToken);
        return answer.Result == MessageBoxResult.Yes;
    }

    // ──────────────────────────── File commands ────────────────────────────

    private async Task NewDocumentAsync()
    {
        if (!await ConfirmDiscardAsync())
        {
            return;
        }

        _state.NewDocument(800, 600);
        _path = null;
        _fileSystem = null;
        _error = null;
        await RenderCanvasAsync();
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
            InitialDirectory = VirtualPath.Parse(CurrentDirectory()),
            Filters = [ImageFilter, new FileDialogFilter(L["Filter.AllFiles"], [], [])],
            RequestedAccess = SelectedFileAccess.ReadWrite
        });
        if (result.Status == FileDialogStatus.Selected && result.Resources.Count > 0)
        {
            SelectedFileResource selected = result.Resources[0];
            await LoadAsync(selected.Path, AppContext.FileSystem.WithSelectedHandle(selected.Handle));
        }
    }

    private async Task SaveAsync()
    {
        if (_path is { } path && _fileSystem is not null)
        {
            await WriteAsync(path, _fileSystem);
        }
        else
        {
            await SaveAsAsync();
        }
    }

    private async Task SaveAsAsync()
    {
        SaveFileDialogResult result = await SaveFileAsync(new SaveFileDialogRequest
        {
            Title = L["Dialog.Save.Title"],
            InitialDirectory = VirtualPath.Parse(CurrentDirectory()),
            SuggestedFileName = _path is { } path ? PaintTitle.FileName(path.Value) : "untitled.png",
            DefaultExtension = ".png",
            Filters =
            [
                new FileDialogFilter(L["Filter.Png"], [".png"], ["image/png"]),
                new FileDialogFilter(L["Filter.Jpeg"], [".jpg", ".jpeg"], ["image/jpeg"])
            ],
            ConfirmOverwrite = true
        });
        if (result.Status == FileDialogStatus.Selected && result.Resource is not null)
        {
            await WriteAsync(result.Resource.Path, AppContext.FileSystem.WithSelectedHandle(result.Resource.Handle));
        }
    }

    /// <summary>Reads, checks and decodes an image, then makes it the document.</summary>
    private async Task LoadAsync(VirtualPath path, IAppFileSystemGateway fileSystem)
    {
        _error = null;
        _busyMessage = L["Busy.Opening", PaintTitle.FileName(path.Value, Untitled)];
        StateHasChanged();
        try
        {
            PaintFileReadResult read = await PaintFileStore.ReadAsync(fileSystem, path, AppContext.CancellationToken);
            if (read.Error is not null || read.Bytes is null || read.Header is null)
            {
                _error = L["Error.Open", path.Value, read.Error ?? L["Error.Unknown"]];
                return;
            }

            DecodedImage decoded = await _module!.InvokeAsync<DecodedImage>(
                "decodeImage", AppContext.CancellationToken, read.Bytes, read.Header.MediaType, ImageFileFormat.MaxDimension);
            if (decoded.Error is not null || decoded.Pixels is null)
            {
                _error = L["Error.Open", path.Value, DescribeDecodeError(decoded)];
                return;
            }

            _state.ReplaceDocument(decoded.Width, decoded.Height, decoded.Pixels);
            _path = path;
            _fileSystem = fileSystem;
            await RenderCanvasAsync();
        }
        catch (Exception exception) when (exception is JSException or ArgumentException)
        {
            _error = L["Error.Open", path.Value, exception.Message];
        }
        finally
        {
            _busyMessage = null;
        }
    }

    /// <summary>Encodes the image for the target's extension and writes it.</summary>
    private async Task WriteAsync(VirtualPath path, IAppFileSystemGateway fileSystem)
    {
        _error = null;
        ImageFileKind kind = ImageFileFormat.SaveKindFor(path.Value);
        string mediaType = ImageFileFormat.MediaTypeOf(kind);
        _busyMessage = L["Busy.Saving", PaintTitle.FileName(path.Value, Untitled)];
        StateHasChanged();
        try
        {
            byte[] encoded = await _module!.InvokeAsync<byte[]>(
                "encodeImage", AppContext.CancellationToken, _state.Width, _state.Height, _state.GetCurrentBuffer(), mediaType, JpegQuality / 100.0);
            string? failure = await PaintFileStore.WriteAsync(fileSystem, path, encoded, mediaType, AppContext.CancellationToken);
            if (failure is not null)
            {
                _error = L["Error.Save", path.Value, failure];
                return;
            }

            _path = path;
            _fileSystem = fileSystem;
            _state.MarkSaved();
        }
        catch (JSException exception)
        {
            _error = L["Error.Save", path.Value, exception.Message];
        }
        finally
        {
            _busyMessage = null;
        }
    }

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!_state.IsDirty)
        {
            return true;
        }

        MessageBoxDialogResult answer = await MessageBox(
            L["Dialog.Unsaved.Title"],
            L["Dialog.Unsaved.Discard", PaintTitle.FileName(_path?.Value, Untitled)],
            MessageBoxType.YesNo);
        return answer.Result == MessageBoxResult.Yes;
    }

    private string CurrentDirectory() =>
        _path is { } path && path.Value.LastIndexOf('/') is > 0 and var slash
            ? path.Value[..slash]
            : KazoUser.Describe(AppContext).HomeDirectory;

    private void DismissError() => _error = null;

    private async Task HandleKeyDownAsync(KeyboardEventArgs args)
    {
        if (!(args.CtrlKey || args.MetaKey) || IsBusy)
        {
            return;
        }

        switch (args.Key.ToLowerInvariant())
        {
            case "s" when args.ShiftKey: await SaveAsAsync(); break;
            case "s": await SaveAsync(); break;
            case "o": await OpenAsync(); break;
            case "n": await NewDocumentAsync(); break;
            case "z": await UndoAsync(); break;
            case "y": await RedoAsync(); break;
        }
    }

    // ──────────────────────────── Drawing ────────────────────────────

    private async Task StartDrawAsync(PointerEventArgs args)
    {
        if (_module is null)
        {
            return;
        }

        _lastX = (int)args.OffsetX;
        _lastY = (int)args.OffsetY;
        await _module.InvokeVoidAsync("capturePointer", _canvas, args.PointerId);
        if (_state.PanMode)
        {
            _panning = true;
            return;
        }

        if (_state.CropMode)
        {
            _cropStart = (_lastX, _lastY);
            return;
        }

        _drawing = true;
        _state.DrawLine(_lastX, _lastY, _lastX, _lastY);
        await RenderCanvasAsync();
    }

    private async Task DrawAsync(PointerEventArgs args)
    {
        int x = (int)args.OffsetX;
        int y = (int)args.OffsetY;
        if (_panning)
        {
            _state.PanBy(x - _lastX, y - _lastY);
            (_lastX, _lastY) = (x, y);
            await ApplyViewportAsync();
            return;
        }

        if (!_drawing)
        {
            return;
        }

        _state.DrawLine(_lastX, _lastY, x, y);
        (_lastX, _lastY) = (x, y);
        await RenderCanvasAsync();
    }

    private async Task StopDrawAsync(PointerEventArgs args)
    {
        _drawing = false;
        _panning = false;
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("releasePointer", _canvas, args.PointerId);
        }

        if (_cropStart is not { } start)
        {
            return;
        }

        _cropStart = null;
        int endX = (int)args.OffsetX;
        int endY = (int)args.OffsetY;
        _state.Crop(Math.Min(start.X, endX), Math.Min(start.Y, endY), Math.Abs(endX - start.X) + 1, Math.Abs(endY - start.Y) + 1);
        await RenderCanvasAsync();
    }

    private async Task UndoAsync() { _state.Undo(); await RenderCanvasAsync(); }

    private async Task RedoAsync() { _state.Redo(); await RenderCanvasAsync(); }

    private async Task RotateAsync() { _state.Rotate90(); await RenderCanvasAsync(); }

    private async Task ToggleGridAsync() { _state.ToggleGrid(); await InvokeAsync(StateHasChanged); }

    private void ToggleCrop() => _state.ToggleCropMode();

    private void TogglePan() => _state.TogglePanMode();

    private async Task ZoomIn() { _state.ZoomIn(); await ApplyViewportAsync(); }

    private async Task ZoomOut() { _state.ZoomOut(); await ApplyViewportAsync(); }

    private async ValueTask RenderCanvasAsync()
    {
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("renderRgba", _canvas, _state.Width, _state.Height, _state.GetCurrentBuffer());
        }
    }

    private async ValueTask ApplyViewportAsync()
    {
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("setViewport", _canvas, _state.PanX, _state.PanY, _state.Scale);
        }
    }

    /// <summary>The root's classes: the high-contrast frame and grid, and a still busy marker (ADR 0105).</summary>
    private string RootClass =>
        "paint-root"
        + (_accessibility.HighContrast ? " is-high-contrast" : string.Empty)
        + (_accessibility.ReducedMotion ? " is-reduced-motion" : string.Empty);

    /// <summary>Reads the session's accessibility preferences; returns whether they changed.</summary>
    private async Task<bool> ReadAccessibilityAsync()
    {
        KazoAccessibilityPreferences next = await AppContext.Accessibility.GetPreferencesAsync(CancellationToken.None);
        bool changed = next != _accessibility;
        _accessibility = next;
        return changed;
    }

    private void OnAccessibilityChanged(object? sender, EventArgs args) => _ = InvokeAsync(async () =>
    {
        if (await ReadAccessibilityAsync())
        {
            StateHasChanged();
        }
    });

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        AppContext.Accessibility.Changed -= OnAccessibilityChanged;
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The page is gone.
            }
        }
    }

    /// <summary>Turns the module's decoding error code into a sentence in the app's language.</summary>
    private string DescribeDecodeError(DecodedImage decoded) => decoded.Error switch
    {
        "too-large" => L["Error.DecodedTooLarge", decoded.Width, decoded.Height, ImageFileFormat.MaxDimension],
        "decode-failed" => L["Error.DecodeFailed"],
        _ => L["Error.Unknown"]
    };

    /// <summary>
    /// Pixels decoded by the module, or why decoding failed: <c>decode-failed</c>, or <c>too-large</c>
    /// with the image's own size in <see cref="Width"/> and <see cref="Height"/>.
    /// </summary>
    private sealed record DecodedImage(int Width, int Height, byte[]? Pixels, string? Error);
}

/// <summary>Formats Hack Paint's window title from the document path and its saved state.</summary>
internal static class PaintTitle
{
    /// <summary>Gets the file name of a path, or <paramref name="untitled"/>.</summary>
    /// <param name="path">Document path or <see langword="null"/>.</param>
    /// <param name="untitled">The name of a document without a file, in the app's language.</param>
    public static string FileName(string? path, string untitled = "Untitled") =>
        string.IsNullOrEmpty(path) ? untitled : path[(path.LastIndexOf('/') + 1)..];

    /// <summary>Builds <c>[*]name — Hack Paint</c> (the product name is not translated).</summary>
    /// <param name="path">Document path or <see langword="null"/>.</param>
    /// <param name="dirty">Whether the image has unsaved changes.</param>
    /// <param name="untitled">The name of a document without a file, in the app's language.</param>
    public static string Format(string? path, bool dirty, string untitled = "Untitled") =>
        $"{(dirty ? "*" : string.Empty)}{FileName(path, untitled)} — Hack Paint";
}
