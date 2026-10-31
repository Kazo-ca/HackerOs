namespace HackerOs.Theming.Abstractions;

/// <summary>One cursor image of a theme (ADR 0205 §1).</summary>
/// <param name="Url">
/// Image address: a <c>data:image/png;base64,</c> URL for a theme read from the files, or a path
/// relative to the document for a built-in theme (<c>_content/HackerOs.Theming.Blazor/cursors/…</c>).
/// </param>
/// <param name="HotspotX">Column of the hot spot, the pixel that points, from the left edge.</param>
/// <param name="HotspotY">Row of the hot spot, from the top edge.</param>
public sealed record CursorImage(string Url, int HotspotX, int HotspotY);

/// <summary>
/// A cursor theme (ADR 0205): a name and one PNG image per cursor name of <see cref="CursorNames"/>.
/// The built-in <c>system</c> theme has no image and keeps the host's cursors.
/// </summary>
/// <param name="Id">Identifier written in <c>cursors.config</c>: lowercase letters, digits and dashes.</param>
/// <param name="Name">Display name.</param>
/// <param name="Origin">Folder of a theme read from the files, or <see langword="null"/> for a built-in theme.</param>
/// <param name="SourcePath">Its <c>cursor.theme</c> file, or <see langword="null"/> for a built-in theme.</param>
/// <param name="Images">Images by cursor name, as the theme declares them.</param>
public sealed record CursorTheme(
    string Id,
    string Name,
    ThemeOrigin? Origin,
    string? SourcePath,
    IReadOnlyDictionary<string, CursorImage> Images)
{
    /// <summary>
    /// Gets the image drawn for a cursor name: its own, or the one of its fallback
    /// (<see cref="CursorNames.GetFallback"/>), or <see langword="null"/> to keep the host's cursor.
    /// </summary>
    /// <param name="name">A cursor name.</param>
    public CursorImage? Resolve(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (string? current = name; current is not null; current = CursorNames.GetFallback(current))
        {
            if (Images.TryGetValue(current, out CursorImage? image))
            {
                return image;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets every cursor name of <see cref="CursorNames.All"/> that this theme draws, fallbacks
    /// resolved: what the shell applies to its surface. Empty for the <c>system</c> theme.
    /// </summary>
    public IReadOnlyDictionary<string, CursorImage> ResolveAll()
    {
        Dictionary<string, CursorImage> resolved = new(StringComparer.Ordinal);
        foreach (string name in CursorNames.All)
        {
            if (Resolve(name) is { } image)
            {
                resolved[name] = image;
            }
        }

        return resolved;
    }
}
