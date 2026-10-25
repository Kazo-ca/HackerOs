namespace HackerOs.Windowing.Abstractions;

/// <summary>Identifies one window independently from its app instance.</summary>
public readonly record struct WindowId
{
    private WindowId(Guid value) => Value = value;

    /// <summary>Gets the underlying non-empty identifier.</summary>
    public Guid Value { get; }

    /// <summary>Creates a window identifier from a non-empty GUID.</summary>
    public static WindowId FromGuid(Guid value) => value != Guid.Empty
        ? new WindowId(value)
        : throw new ArgumentOutOfRangeException(nameof(value), "Window ID cannot be empty.");

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Describes a window rectangle in CSS pixels.</summary>
public readonly record struct WindowBounds
{
    /// <summary>Creates validated window bounds.</summary>
    public WindowBounds(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Window coordinates must be finite.");
        }

        if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(height) || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Window dimensions must be finite and positive.");
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>Gets the left coordinate.</summary>
    public double X { get; }

    /// <summary>Gets the top coordinate.</summary>
    public double Y { get; }

    /// <summary>Gets the width.</summary>
    public double Width { get; }

    /// <summary>Gets the height.</summary>
    public double Height { get; }
}

/// <summary>Defines resize capabilities and dimensions for a window.</summary>
public sealed record WindowConstraints
{
    /// <summary>Creates validated window constraints.</summary>
    public WindowConstraints(
        bool isResizable,
        double minWidth,
        double minHeight,
        double? maxWidth = null,
        double? maxHeight = null,
        bool isMovable = true)
    {
        if (!double.IsFinite(minWidth) || minWidth <= 0 || !double.IsFinite(minHeight) || minHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minWidth), "Minimum dimensions must be finite and positive.");
        }

        if (maxWidth is <= 0 || maxWidth < minWidth || maxWidth is double.NaN or double.PositiveInfinity or double.NegativeInfinity)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWidth), "Maximum width must be finite and no smaller than minimum width.");
        }

        if (maxHeight is <= 0 || maxHeight < minHeight || maxHeight is double.NaN or double.PositiveInfinity or double.NegativeInfinity)
        {
            throw new ArgumentOutOfRangeException(nameof(maxHeight), "Maximum height must be finite and no smaller than minimum height.");
        }

        IsResizable = isResizable;
        MinWidth = minWidth;
        MinHeight = minHeight;
        MaxWidth = maxWidth;
        MaxHeight = maxHeight;
        IsMovable = isMovable;
    }

    /// <summary>Gets whether user resize commands are allowed.</summary>
    public bool IsResizable { get; }

    /// <summary>
    /// Gets whether user move commands are allowed. Defaults to <see langword="true"/> for source
    /// compatibility with every existing caller; a pinned surface (e.g. Mobile's single full-screen
    /// window, doc/wasm/dev/old/mobile-interface-platform-plan.md §7.1) sets this <see langword="false"/>.
    /// </summary>
    public bool IsMovable { get; }

    /// <summary>Gets the minimum width.</summary>
    public double MinWidth { get; }

    /// <summary>Gets the minimum height.</summary>
    public double MinHeight { get; }

    /// <summary>Gets the optional maximum width.</summary>
    public double? MaxWidth { get; }

    /// <summary>Gets the optional maximum height.</summary>
    public double? MaxHeight { get; }
}
