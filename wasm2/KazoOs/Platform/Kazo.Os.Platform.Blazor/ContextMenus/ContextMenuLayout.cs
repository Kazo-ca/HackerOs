using Kazo.Os.AppSdk.Blazor.ContextMenus;

namespace Kazo.Os.Platform.Blazor.ContextMenus;

/// <summary>One drawn row of a menu level: a visible entry, or a separator between two groups.</summary>
/// <param name="Item">The entry, or <see langword="null"/> for a separator.</param>
/// <param name="Submenu">The entry's arranged submenu, when it opens one.</param>
public sealed record ContextMenuRow(KazoMenuItemDefinition? Item, ContextMenuLevel? Submenu)
{
    /// <summary>The separator row.</summary>
    public static readonly ContextMenuRow Separator = new(null, null);

    /// <summary>Gets whether this row is a separator.</summary>
    public bool IsSeparator => Item is null;

    /// <summary>Gets whether the row can take the highlight: an entry, enabled.</summary>
    public bool IsSelectable => Item is { IsDisabled: false };
}

/// <summary>A menu level as drawn: its rows, separators already collapsed (ADR 0150 §3).</summary>
/// <param name="Rows">The rows, never starting or ending with a separator, never two in a row.</param>
public sealed record ContextMenuLevel(IReadOnlyList<ContextMenuRow> Rows);

/// <summary>
/// Turns an app's menu into what the OS draws (ADR 0150 §3): hidden entries and empty groups go, a
/// submenu with no visible entry hides its parent, and a separator is kept only between two visible
/// groups, at every level.
/// </summary>
public static class ContextMenuLayout
{
    /// <summary>Arranges a menu level.</summary>
    /// <param name="groups">The level's groups, in order.</param>
    /// <returns>The level, or <see langword="null"/> when nothing in it is visible.</returns>
    public static ContextMenuLevel? Arrange(IEnumerable<KazoMenuGroupDefinition> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        List<ContextMenuRow> rows = [];
        foreach (KazoMenuGroupDefinition group in groups)
        {
            List<ContextMenuRow> visible = [];
            foreach (KazoMenuItemDefinition item in group.Items)
            {
                if (!item.IsVisible)
                {
                    continue;
                }

                if (item.Children.Count == 0)
                {
                    visible.Add(new ContextMenuRow(item, null));
                }
                else if (Arrange(item.Children) is { } submenu)
                {
                    visible.Add(new ContextMenuRow(item, submenu));
                }
            }

            if (visible.Count == 0)
            {
                continue;
            }

            if (rows.Count > 0)
            {
                rows.Add(ContextMenuRow.Separator);
            }

            rows.AddRange(visible);
        }

        return rows.Count == 0 ? null : new ContextMenuLevel(rows);
    }

    /// <summary>
    /// Finds the next row that can take the highlight, in <paramref name="direction"/>, wrapping
    /// around and skipping separators and disabled entries (ADR 0150 §6).
    /// </summary>
    /// <param name="level">The level.</param>
    /// <param name="from">The current row, or -1 (or the row count) to start from an end.</param>
    /// <param name="direction">+1 down, -1 up.</param>
    /// <returns>The row index, or -1 when no row can take it.</returns>
    public static int NextSelectable(ContextMenuLevel level, int from, int direction)
    {
        ArgumentNullException.ThrowIfNull(level);
        int count = level.Rows.Count;
        for (int step = 1; step <= count; step++)
        {
            int index = (((from + (direction * step)) % count) + count) % count;
            if (level.Rows[index].IsSelectable)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Finds the next selectable row, after <paramref name="from"/>, whose title starts with
    /// <paramref name="letter"/> (case-insensitive), as Windows' menus do.
    /// </summary>
    /// <param name="level">The level.</param>
    /// <param name="from">The current row, or -1.</param>
    /// <param name="letter">The typed character.</param>
    /// <returns>The row index, or -1.</returns>
    public static int FindByLetter(ContextMenuLevel level, int from, char letter)
    {
        ArgumentNullException.ThrowIfNull(level);
        int count = level.Rows.Count;
        for (int step = 1; step <= count; step++)
        {
            int index = (((from + step) % count) + count) % count;
            ContextMenuRow row = level.Rows[index];
            if (row.IsSelectable
                && row.Item!.Title.TrimStart() is { Length: > 0 } title
                && char.ToUpperInvariant(title[0]) == char.ToUpperInvariant(letter))
            {
                return index;
            }
        }

        return -1;
    }
}
