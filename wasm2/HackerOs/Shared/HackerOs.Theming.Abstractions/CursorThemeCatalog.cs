namespace HackerOs.Theming.Abstractions;

/// <summary>
/// The built-in cursor themes (ADR 0205 §2): <c>system</c> (the host's own cursors, the default)
/// and two drawn sets whose PNG files ship in <c>HackerOs.Theming.Blazor</c>
/// (<c>wwwroot/cursors/&lt;id&gt;/</c>, drawn by <c>build/tools/generate-cursor-themes.py</c>).
/// </summary>
public static class CursorThemeCatalog
{
    /// <summary>The host's own cursors: no image, nothing changed on the surface.</summary>
    public const string SystemId = "system";

    /// <summary>Green neon outline on black, matching the HackerOS theme.</summary>
    public const string NeonId = "hackeros-neon";

    /// <summary>Crimson outline on black with bone details, for the gothic themes.</summary>
    public const string CrimsonId = "gothic-crimson";

    /// <summary>Folder of the built-in images, relative to the document.</summary>
    public const string AssetRoot = "_content/HackerOs.Theming.Blazor/cursors";

    // Hot spots of the drawings of generate-cursor-themes.py (32 x 32 images): change both together.
    private static readonly (string Name, int X, int Y)[] DrawnCursors =
    [
        (CursorNames.Default, 2, 1),
        (CursorNames.Pointer, 11, 1),
        (CursorNames.Text, 16, 16),
        (CursorNames.Wait, 16, 16),
        (CursorNames.Progress, 2, 1),
        ("help", 2, 1),
        ("crosshair", 16, 16),
        ("move", 16, 16),
        ("not-allowed", 16, 16),
        ("grab", 16, 16),
        ("grabbing", 16, 16),
        ("ns-resize", 16, 16),
        ("ew-resize", 16, 16),
        ("nesw-resize", 16, 16),
        ("nwse-resize", 16, 16)
    ];

    /// <summary>Gets the built-in themes, <c>system</c> first.</summary>
    public static IReadOnlyList<CursorTheme> Themes { get; } =
    [
        new(SystemId, "System", null, null, new Dictionary<string, CursorImage>(StringComparer.Ordinal)),
        Drawn(NeonId, "HackerOS Neon"),
        Drawn(CrimsonId, "Gothic Crimson")
    ];

    /// <summary>Gets the <c>system</c> theme.</summary>
    public static CursorTheme System => Themes[0];

    /// <summary>Finds a built-in theme by id.</summary>
    /// <param name="id">Theme id.</param>
    /// <param name="theme">The theme when found.</param>
    public static bool TryGet(string? id, out CursorTheme theme)
    {
        theme = Themes.FirstOrDefault(candidate => candidate.Id == id)!;
        return theme is not null;
    }

    private static CursorTheme Drawn(string id, string name) => new(
        id,
        name,
        null,
        null,
        DrawnCursors.ToDictionary(
            static cursor => cursor.Name,
            cursor => new CursorImage($"{AssetRoot}/{id}/{cursor.Name}.png", cursor.X, cursor.Y),
            StringComparer.Ordinal));
}
