namespace HackerOs.Theming.Abstractions;

/// <summary>
/// The cursor names a cursor theme may draw (ADR 0205 §1): the CSS <c>cursor</c> keywords, which
/// are what the OS surface and every app already ask for. A theme maps a name to a PNG image; a
/// name the theme does not draw falls back along <see cref="GetFallback"/> (like the symbolic links
/// of an X cursor theme) and, when nothing matches, keeps the host's own cursor.
/// </summary>
public static class CursorNames
{
    /// <summary>The ordinary arrow.</summary>
    public const string Default = "default";

    /// <summary>A link or a button: the pointing hand.</summary>
    public const string Pointer = "pointer";

    /// <summary>Text that can be selected or typed: the I-beam.</summary>
    public const string Text = "text";

    /// <summary>The system is busy and takes no input.</summary>
    public const string Wait = "wait";

    /// <summary>The system is busy but still takes input.</summary>
    public const string Progress = "progress";

    /// <summary>The names, in the order Settings shows them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Default, Pointer, Text, Wait, Progress, "help", "crosshair", "move", "not-allowed", "grab", "grabbing",
        "ns-resize", "ew-resize", "nesw-resize", "nwse-resize", "n-resize", "s-resize", "e-resize", "w-resize",
        "ne-resize", "nw-resize", "se-resize", "sw-resize", "col-resize", "row-resize", "all-scroll", "no-drop",
        "copy", "alias", "context-menu", "cell", "vertical-text", "zoom-in", "zoom-out"
    ];

    /// <summary>The names Settings shows as a theme's preview.</summary>
    public static IReadOnlyList<string> Preview { get; } = [Default, Pointer, Text, Wait, "move", "ew-resize"];

    private static readonly HashSet<string> Known = new(All, StringComparer.Ordinal);

    private static readonly Dictionary<string, string> Fallbacks = new(StringComparer.Ordinal)
    {
        ["n-resize"] = "ns-resize",
        ["s-resize"] = "ns-resize",
        ["row-resize"] = "ns-resize",
        ["e-resize"] = "ew-resize",
        ["w-resize"] = "ew-resize",
        ["col-resize"] = "ew-resize",
        ["ne-resize"] = "nesw-resize",
        ["sw-resize"] = "nesw-resize",
        ["nw-resize"] = "nwse-resize",
        ["se-resize"] = "nwse-resize",
        ["all-scroll"] = "move",
        ["grabbing"] = "grab",
        ["progress"] = Wait,
        ["no-drop"] = "not-allowed",
        ["vertical-text"] = Text,
        ["cell"] = "crosshair"
    };

    /// <summary>Checks that a name is one of <see cref="All"/>.</summary>
    /// <param name="name">Candidate name.</param>
    public static bool IsKnown(string? name) => name is not null && Known.Contains(name);

    /// <summary>
    /// Gets the name drawn instead of <paramref name="name"/> when a theme lacks it, or
    /// <see langword="null"/> when the host's cursor is kept (for example <c>copy</c>, whose
    /// meaning an arrow would lose).
    /// </summary>
    /// <param name="name">A name of <see cref="All"/>.</param>
    public static string? GetFallback(string name) => Fallbacks.GetValueOrDefault(name);
}
