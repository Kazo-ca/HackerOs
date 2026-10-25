namespace HackerOs.Apps.HackPaint;

/// <summary>
/// Owns the complete editable Hack Paint document as RGBA pixels. Every visual
/// operation commits a copied pixel snapshot, so undo and redo always restore
/// the same content that the renderer displays.
/// </summary>
public sealed class PaintCanvasState
{
    private const int MaxHistory = 20;
    private sealed record CanvasSnapshot(int Width, int Height, byte[] Buffer, long Id);
    private readonly List<CanvasSnapshot> _history = [];
    private int _historyIndex = -1;
    private long _nextSnapshotId;
    private long _savedSnapshotId = -1;

    /// <summary>Starts with a blank white 800 x 600 document.</summary>
    public PaintCanvasState() => NewDocument(800, 600);

    /// <summary>Width of the current image in pixels (16 to 4096 for a new document).</summary>
    public int Width { get; private set; }
    /// <summary>Height of the current image in pixels (16 to 4096 for a new document).</summary>
    public int Height { get; private set; }
    /// <summary>The brush color as <c>#rrggbb</c>; an unparsable value paints the default green.</summary>
    public string ActiveColor { get; set; } = "#33ff33";
    /// <summary>The brush diameter in pixels.</summary>
    public int BrushSize { get; set; } = 5;
    /// <summary>The display zoom factor, from 0.2 to 5; it never changes the document pixels.</summary>
    public double Scale { get; private set; } = 1.0;
    /// <summary>True when the pixel grid overlay is drawn over the canvas.</summary>
    public bool ShowGrid { get; private set; }
    /// <summary>True when dragging pans the viewport instead of painting.</summary>
    public bool PanMode { get; private set; }
    /// <summary>True when dragging selects a crop rectangle instead of painting.</summary>
    public bool CropMode { get; private set; }
    /// <summary>Horizontal viewport offset in screen pixels, changed by <see cref="PanBy"/>.</summary>
    public int PanX { get; private set; }
    /// <summary>Vertical viewport offset in screen pixels, changed by <see cref="PanBy"/>.</summary>
    public int PanY { get; private set; }
    /// <summary>True when an earlier snapshot exists in the history (at most 20 are kept).</summary>
    public bool CanUndo => _historyIndex > 0;
    /// <summary>True after an undo, until a new commit discards the redo branch.</summary>
    public bool CanRedo => _historyIndex >= 0 && _historyIndex < _history.Count - 1;

    /// <summary>
    /// Gets whether the displayed image differs from the last one opened or saved. Undoing back to
    /// the saved image makes the document clean again, as in a real editor.
    /// </summary>
    public bool IsDirty => _historyIndex >= 0 && _history[_historyIndex].Id != _savedSnapshotId;

    /// <summary>Records the displayed image as the saved one (after Open or Save).</summary>
    public void MarkSaved() => _savedSnapshotId = _history[_historyIndex].Id;

    /// <summary>Creates a new bounded image and resets history to that image.</summary>
    public void NewDocument(int width, int height, byte r = 255, byte g = 255, byte b = 255, byte a = 255)
    {
        Width = Math.Clamp(width, 16, 4096);
        Height = Math.Clamp(height, 16, 4096);
        Scale = 1.0;
        PanX = 0;
        PanY = 0;
        _history.Clear();
        _historyIndex = -1;
        Commit(Width, Height, CreateBlankBuffer(Width, Height, r, g, b, a));
        MarkSaved();
    }

    /// <summary>Imports an already-decoded RGBA image after validating its size; it becomes the saved image.</summary>
    public void ReplaceDocument(int width, int height, byte[] pixelRgba)
    {
        ValidateBuffer(width, height, pixelRgba);
        _history.Clear();
        _historyIndex = -1;
        Commit(width, height, pixelRgba);
        MarkSaved();
    }

    /// <summary>Returns a defensive copy so callers cannot mutate history.</summary>
    public byte[] GetCurrentBuffer() => _history[_historyIndex].Buffer.ToArray();

    /// <summary>Commits a copy of an edited RGBA buffer of the current size as a new undo step.</summary>
    /// <param name="pixelRgba">The full image, 4 bytes per pixel; its length must match <see cref="Width"/> x <see cref="Height"/>.</param>
    public void PushHistory(byte[] pixelRgba) => Commit(Width, Height, pixelRgba);
    /// <summary>Commits a copy of an edited RGBA buffer that may change the image size, as a new undo step.</summary>
    /// <param name="width">The new image width in pixels (1 to 4096).</param>
    /// <param name="height">The new image height in pixels (1 to 4096).</param>
    /// <param name="pixelRgba">The full image, 4 bytes per pixel.</param>
    public void PushHistory(int width, int height, byte[] pixelRgba) => Commit(width, height, pixelRgba);

    /// <summary>Draws a round, opaque RGBA brush stroke into the authoritative document.</summary>
    public void DrawLine(int startX, int startY, int endX, int endY, string? color = null, int? size = null)
    {
        var pixels = GetCurrentBuffer();
        var (r, g, b, a) = ParseColor(color ?? ActiveColor);
        var radius = Math.Max(0, (size ?? BrushSize - 1) / 2);
        var dx = Math.Abs(endX - startX);
        var sx = startX < endX ? 1 : -1;
        var dy = -Math.Abs(endY - startY);
        var sy = startY < endY ? 1 : -1;
        var error = dx + dy;
        while (true)
        {
            PaintCircle(pixels, startX, startY, radius, r, g, b, a);
            if (startX == endX && startY == endY) break;
            var twiceError = 2 * error;
            if (twiceError >= dy) { error += dy; startX += sx; }
            if (twiceError <= dx) { error += dx; startY += sy; }
        }
        Commit(Width, Height, pixels);
    }

    /// <summary>Crops the current image to a clamped non-empty rectangle.</summary>
    public void Crop(int x, int y, int width, int height)
    {
        var left = Math.Clamp(x, 0, Width - 1);
        var top = Math.Clamp(y, 0, Height - 1);
        var right = Math.Clamp(x + Math.Max(1, width), left + 1, Width);
        var bottom = Math.Clamp(y + Math.Max(1, height), top + 1, Height);
        var cropped = new byte[(right - left) * (bottom - top) * 4];
        var source = _history[_historyIndex].Buffer;
        for (var row = 0; row < bottom - top; row++)
            Buffer.BlockCopy(source, ((top + row) * Width + left) * 4, cropped, row * (right - left) * 4, (right - left) * 4);
        Commit(right - left, bottom - top, cropped);
    }

    /// <summary>Steps back to the previous snapshot, restoring its size as well as its pixels.</summary>
    public void Undo() { if (CanUndo) Apply(--_historyIndex); }
    /// <summary>Steps forward to the snapshot undone last.</summary>
    public void Redo() { if (CanRedo) Apply(++_historyIndex); }
    /// <summary>Zooms in by 25 percent, up to 5x.</summary>
    public void ZoomIn() => Scale = Math.Min(5, Math.Round(Scale * 1.25, 2));
    /// <summary>Zooms out by 20 percent, down to 0.2x.</summary>
    public void ZoomOut() => Scale = Math.Max(.2, Math.Round(Scale * .8, 2));
    /// <summary>Shows or hides the pixel grid overlay.</summary>
    public void ToggleGrid() => ShowGrid = !ShowGrid;
    /// <summary>Switches dragging between painting and panning the viewport.</summary>
    public void TogglePanMode() => PanMode = !PanMode;
    /// <summary>Switches dragging between painting and selecting a crop rectangle.</summary>
    public void ToggleCropMode() => CropMode = !CropMode;
    /// <summary>Moves only the rendered viewport; document pixels remain unchanged.</summary>
    public void PanBy(int deltaX, int deltaY) { PanX += deltaX; PanY += deltaY; }

    /// <summary>Rotates the actual pixels clockwise, retaining them in history.</summary>
    /// <remarks>
    /// Stays on the window's thread rather than in a HackBackground task (ADR 0140, see
    /// doc/en/wasm/framework/background-jobs.md): Hack Paint has no filter yet, and its whole-image operations (rotate,
    /// crop) are one pass of copies over the buffer. A 4096×4096 image is 64 MiB of RGBA, four times
    /// what a task's input may carry as JSON (16 MiB, ADR 0128 section 4), and the copy back would cost
    /// more than the pass itself. A future filter goes through a task with a temporary file instead.
    /// </remarks>
    public void Rotate90()
    {
        var source = _history[_historyIndex].Buffer;
        var rotated = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
            Buffer.BlockCopy(source, (y * Width + x) * 4, rotated, (x * Height + Height - 1 - y) * 4, 4);
        Commit(Height, Width, rotated);
    }

    /// <summary>Allocates an RGBA buffer filled with one color.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="r">Red component of the fill color.</param>
    /// <param name="g">Green component of the fill color.</param>
    /// <param name="b">Blue component of the fill color.</param>
    /// <param name="a">Alpha component of the fill color.</param>
    /// <returns>A new buffer of <c>width * height * 4</c> bytes.</returns>
    public static byte[] CreateBlankBuffer(int width, int height, byte r, byte g, byte b, byte a)
    {
        var result = new byte[checked(width * height * 4)];
        for (var index = 0; index < result.Length; index += 4)
            (result[index], result[index + 1], result[index + 2], result[index + 3]) = (r, g, b, a);
        return result;
    }

    private void Commit(int width, int height, byte[] pixels)
    {
        ValidateBuffer(width, height, pixels);
        if (_historyIndex < _history.Count - 1) _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(new CanvasSnapshot(width, height, pixels.ToArray(), _nextSnapshotId++));
        if (_history.Count > MaxHistory) _history.RemoveAt(0);
        _historyIndex = _history.Count - 1;
        Apply(_historyIndex);
    }

    private void Apply(int index) { Width = _history[index].Width; Height = _history[index].Height; }
    private static void ValidateBuffer(int width, int height, byte[] pixels)
    {
        if (width is < 1 or > 4096 || height is < 1 or > 4096 || pixels.Length != checked(width * height * 4))
            throw new ArgumentException("The RGBA buffer does not match the bounded document dimensions.", nameof(pixels));
    }

    private void PaintCircle(byte[] pixels, int centerX, int centerY, int radius, byte r, byte g, byte b, byte a)
    {
        for (var y = centerY - radius; y <= centerY + radius; y++)
        for (var x = centerX - radius; x <= centerX + radius; x++)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height || (x - centerX) * (x - centerX) + (y - centerY) * (y - centerY) > radius * radius) continue;
            var index = (y * Width + x) * 4;
            (pixels[index], pixels[index + 1], pixels[index + 2], pixels[index + 3]) = (r, g, b, a);
        }
    }

    private static (byte R, byte G, byte B, byte A) ParseColor(string value)
    {
        var hex = value.TrimStart('#');
        return hex.Length == 6 && byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(hex[4..], System.Globalization.NumberStyles.HexNumber, null, out var b)
            ? (r, g, b, (byte)255) : ((byte)51, (byte)255, (byte)51, (byte)255);
    }
}
