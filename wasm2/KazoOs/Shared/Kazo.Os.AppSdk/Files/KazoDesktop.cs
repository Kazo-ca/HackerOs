using System.Globalization;

namespace Kazo.Os.AppSdk.Files;

/// <summary>
/// One cell of the desktop grid (ADR 0152 §2, ADR 0160 §3): cells run down each column, then the
/// columns left to right, as on Windows. Written <c>column,row</c> in <c>desktop.config</c>, both from 0.
/// </summary>
/// <param name="Column">The column, from 0 at the left.</param>
/// <param name="Row">The row, from 0 at the top.</param>
public readonly record struct KazoDesktopCell(int Column, int Row)
{
    /// <summary>Reads <c>column,row</c>; <see langword="false"/> for anything else or a negative number.</summary>
    /// <param name="text">The value of a <c>[positions]</c> line.</param>
    /// <param name="cell">The cell read.</param>
    public static bool TryParse(string? text, out KazoDesktopCell cell)
    {
        cell = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Split(',');
        if (parts.Length != 2
            || !int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int column)
            || !int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int row))
        {
            return false;
        }

        cell = new KazoDesktopCell(column, row);
        return true;
    }

    /// <summary>Gets the <c>column,row</c> form written in <c>desktop.config</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Column},{Row}");
}

/// <summary>The size of the desktop icons (Settings ▸ Desktop, the desktop's "View" submenu).</summary>
public enum KazoDesktopIconSize
{
    /// <summary>Small icons: 76 × 84 pixel cells.</summary>
    Small = 0,

    /// <summary>Medium icons (default): 92 × 100 pixel cells.</summary>
    Medium = 1,

    /// <summary>Large icons: 116 × 128 pixel cells.</summary>
    Large = 2
}

/// <summary>How new items, and every item after "Sort by", are ordered on the desktop (ADR 0160 §3).</summary>
public enum KazoDesktopSort
{
    /// <summary>By name; folders first.</summary>
    Name = 0,

    /// <summary>By size, smallest first; folders first, by name.</summary>
    Size = 1,

    /// <summary>By extension, then name; folders first.</summary>
    Type = 2,

    /// <summary>By last change, newest first; folders first.</summary>
    Date = 3
}

/// <summary>
/// The <c>[view]</c> section of <c>desktop.config</c> (ADR 0152 §3) as the file control's desktop mode
/// receives it (<c>KazoFileView.DesktopOptions</c>).
/// </summary>
/// <param name="IconSize">The size of the icons, hence of the cells.</param>
/// <param name="AutoArrange">Whether the icons are kept packed, in the order of their positions.</param>
/// <param name="AlignToGrid">Whether an icon outside the screen is shown on the free cell nearest to its position (else on the last free cell).</param>
/// <param name="Sort">The order of new items.</param>
/// <param name="ShowHome">Whether the Home system icon is shown.</param>
/// <param name="ShowTrash">Whether the Trash system icon is shown.</param>
public sealed record KazoDesktopOptions(
    KazoDesktopIconSize IconSize = KazoDesktopIconSize.Medium,
    bool AutoArrange = false,
    bool AlignToGrid = true,
    KazoDesktopSort Sort = KazoDesktopSort.Name,
    bool ShowHome = true,
    bool ShowTrash = true)
{
    /// <summary>Gets the defaults of <c>desktop.config</c>.</summary>
    public static KazoDesktopOptions Default { get; } = new();
}

/// <summary>
/// The keys of the desktop's system icons (ADR 0152 §4): virtual entries the desktop adds, not files of
/// <c>~/Desktop</c>. They take a cell in <c>[positions]</c> like a file name, and cannot clash with one
/// (no file name starts with <c>::</c> on the desktop: the colons are encoded in the file).
/// </summary>
public static class KazoDesktopItems
{
    /// <summary>The Home icon (<c>~</c>).</summary>
    public const string Home = "::home";

    /// <summary>The Trash icon (<c>~/.local/share/Trash</c>).</summary>
    public const string Trash = "::trash";

    /// <summary>Gets whether <paramref name="key"/> is one of the system icons.</summary>
    /// <param name="key">A position key.</param>
    public static bool IsSystemItem(string? key) => key is Home or Trash;

    /// <summary>
    /// The intent the shell sends to the running desktop app for "Send to ▸ Desktop" (ADR 0160 §6), with
    /// <c>{"appId":"…"}</c>: the desktop writes a shortcut to that app in <c>~/Desktop</c>.
    /// </summary>
    public const string CreateShortcutIntent = "org.hackeros.desktop.create-shortcut.v1";
}

/// <summary>One item to order for the desktop (<see cref="KazoDesktopLayout.Order"/>).</summary>
/// <param name="Key">The position key: the file name.</param>
/// <param name="IsDirectory">Whether it is a folder.</param>
/// <param name="Size">Size in bytes of a file.</param>
/// <param name="ModifiedAt">When its content last changed.</param>
public sealed record KazoDesktopSortItem(string Key, bool IsDirectory, long Size, DateTimeOffset ModifiedAt);

/// <summary>
/// Where each desktop icon is shown, and which positions to write (<see cref="KazoDesktopLayout.Arrange"/>).
/// </summary>
/// <param name="Displayed">The cell each item is drawn on.</param>
/// <param name="Positions">
/// The positions of the items shown, as <c>[positions]</c> should hold them: the saved one of an item
/// kept off screen is unchanged, a new item has its first cell.
/// </param>
/// <param name="Changed">Whether <paramref name="Positions"/> differs from what was saved for these items.</param>
public sealed record KazoDesktopArrangement(
    IReadOnlyDictionary<string, KazoDesktopCell> Displayed,
    IReadOnlyDictionary<string, KazoDesktopCell> Positions,
    bool Changed);

/// <summary>
/// The desktop grid of ADR 0152 §2-3, as pure functions (ADR 0160 §3): every provider of the file
/// control places and moves the icons the same way, and the rules are tested once.
/// </summary>
/// <remarks>
/// <para>Cells run down a column, then to the next column on the right. The linear index of a cell is
/// <c>column × rows + row</c>.</para>
/// <para>Free layout: an item keeps its saved cell; an item without one, or whose cell another item
/// already holds, takes the first free cell (and that is saved); an item saved outside the grid is
/// shown elsewhere without changing what is saved. Auto-arrange: the items are packed without holes,
/// in the order of their saved cells, new items last.</para>
/// </remarks>
public static class KazoDesktopLayout
{
    /// <summary>Gets the cell size, in CSS pixels, of an icon size: (width, height).</summary>
    /// <param name="size">The icon size.</param>
    public static (int Width, int Height) CellSize(KazoDesktopIconSize size) => size switch
    {
        KazoDesktopIconSize.Small => (76, 84),
        KazoDesktopIconSize.Large => (116, 128),
        _ => (92, 100)
    };

    /// <summary>
    /// Orders the items for placement: the system icons first (Home, then Trash, when shown), then the
    /// files by <paramref name="sort"/>, folders first.
    /// </summary>
    /// <param name="items">The files of the desktop folder.</param>
    /// <param name="sort">The sort of <c>[view]</c>.</param>
    /// <param name="showHome">Whether the Home icon is shown.</param>
    /// <param name="showTrash">Whether the Trash icon is shown.</param>
    /// <returns>Every key, in the order new items take cells.</returns>
    public static IReadOnlyList<string> Order(IEnumerable<KazoDesktopSortItem> items, KazoDesktopSort sort, bool showHome, bool showTrash)
    {
        ArgumentNullException.ThrowIfNull(items);
        List<string> keys = [];
        if (showHome)
        {
            keys.Add(KazoDesktopItems.Home);
        }

        if (showTrash)
        {
            keys.Add(KazoDesktopItems.Trash);
        }

        IOrderedEnumerable<KazoDesktopSortItem> ordered = items.OrderBy(static item => item.IsDirectory ? 0 : 1);
        ordered = sort switch
        {
            KazoDesktopSort.Size => ordered.ThenBy(static item => item.IsDirectory ? 0 : item.Size),
            KazoDesktopSort.Type => ordered.ThenBy(static item => item.IsDirectory ? string.Empty : ExtensionOf(item.Key), StringComparer.OrdinalIgnoreCase),
            KazoDesktopSort.Date => ordered.ThenByDescending(static item => item.ModifiedAt),
            _ => ordered
        };
        keys.AddRange(ordered
            .ThenBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Key, StringComparer.Ordinal)
            .Select(static item => item.Key));
        return keys;
    }

    /// <summary>Places every item on a grid of <paramref name="columns"/> × <paramref name="rows"/> cells.</summary>
    /// <param name="keys">Every item shown, in the order new items take cells (<see cref="Order"/>).</param>
    /// <param name="saved">The positions read from <c>[positions]</c>; may name items no longer there.</param>
    /// <param name="columns">Columns that fit on the desktop; at least 1.</param>
    /// <param name="rows">Rows that fit on the desktop; at least 1.</param>
    /// <param name="autoArrange">Whether the items are packed.</param>
    /// <param name="alignToGrid">Whether an item saved off screen goes to the nearest free cell (else to the last one).</param>
    public static KazoDesktopArrangement Arrange(
        IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, KazoDesktopCell>? saved,
        int columns,
        int rows,
        bool autoArrange,
        bool alignToGrid)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        saved ??= new Dictionary<string, KazoDesktopCell>();
        string[] distinct = [.. keys.Distinct(StringComparer.Ordinal)];

        Dictionary<string, KazoDesktopCell> displayed = new(StringComparer.Ordinal);
        Dictionary<string, KazoDesktopCell> positions = new(StringComparer.Ordinal);
        if (autoArrange)
        {
            int index = 0;
            foreach (string key in PackingOrder(distinct, saved, rows))
            {
                KazoDesktopCell cell = CellAt(index++, rows);
                displayed[key] = cell;
                positions[key] = cell;
            }

            return new KazoDesktopArrangement(displayed, positions, Differs(positions, saved));
        }

        HashSet<KazoDesktopCell> taken = [];
        List<string> needCell = [];
        List<string> offScreen = [];
        foreach (string key in distinct)
        {
            if (!saved.TryGetValue(key, out KazoDesktopCell cell))
            {
                needCell.Add(key);
            }
            else if (!Inside(cell, columns, rows))
            {
                offScreen.Add(key);
            }
            else if (!taken.Add(cell))
            {
                // Two items saved on one cell (an edit by hand): the second moves, and that is saved.
                needCell.Add(key);
            }
            else
            {
                displayed[key] = cell;
                positions[key] = cell;
            }
        }

        // Order matters: the order of keys, not the order of discovery, decides who gets the first cells.
        foreach (string key in distinct.Where(needCell.Contains))
        {
            KazoDesktopCell cell = FirstFree(taken, columns, rows, start: 0);
            taken.Add(cell);
            displayed[key] = cell;
            if (Inside(cell, columns, rows))
            {
                positions[key] = cell;
            }
        }

        foreach (string key in offScreen)
        {
            KazoDesktopCell wanted = saved[key];
            KazoDesktopCell cell = alignToGrid ? NearestFree(taken, wanted, columns, rows) : LastFree(taken, columns, rows);
            taken.Add(cell);
            displayed[key] = cell;
            // ADR 0152 §3: shown elsewhere, its saved position is kept for a larger screen.
            positions[key] = wanted;
        }

        return new KazoDesktopArrangement(displayed, positions, Differs(positions, saved));
    }

    /// <summary>
    /// Moves <paramref name="moved"/> so that <paramref name="anchor"/> lands on <paramref name="target"/>
    /// (a drag on the desktop), and returns the positions to save.
    /// </summary>
    /// <param name="current">The arrangement shown before the drag.</param>
    /// <param name="moved">The items dragged.</param>
    /// <param name="anchor">The item held by the pointer; one of <paramref name="moved"/>.</param>
    /// <param name="target">The cell under the pointer at the drop.</param>
    /// <param name="columns">Columns of the grid.</param>
    /// <param name="rows">Rows of the grid.</param>
    /// <param name="autoArrange">
    /// Whether the items are packed: the dragged items are then inserted at the target's place in the
    /// order, the others shift.
    /// </param>
    public static IReadOnlyDictionary<string, KazoDesktopCell> Move(
        KazoDesktopArrangement current,
        IReadOnlyList<string> moved,
        string anchor,
        KazoDesktopCell target,
        int columns,
        int rows,
        bool autoArrange)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(moved);
        ArgumentException.ThrowIfNullOrEmpty(anchor);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        HashSet<string> movedSet = new(moved.Where(current.Displayed.ContainsKey), StringComparer.Ordinal);
        target = Clamp(target, columns, rows);
        if (movedSet.Count == 0 || !current.Displayed.TryGetValue(anchor, out KazoDesktopCell anchorCell))
        {
            return current.Positions;
        }

        if (autoArrange)
        {
            List<string> order = [.. current.Displayed
                .OrderBy(pair => Linear(pair.Value, rows))
                .Select(static pair => pair.Key)
                .Where(key => !movedSet.Contains(key))];
            string[] dragged = [.. current.Displayed
                .Where(pair => movedSet.Contains(pair.Key))
                .OrderBy(pair => Linear(pair.Value, rows))
                .Select(static pair => pair.Key)];
            // The items before the target cell in the remaining order stay before the dragged ones.
            int insertAt = Math.Min(order.Count, current.Displayed
                .Where(pair => !movedSet.Contains(pair.Key) && Linear(pair.Value, rows) < Linear(target, rows))
                .Count());
            order.InsertRange(insertAt, dragged);
            Dictionary<string, KazoDesktopCell> packed = new(StringComparer.Ordinal);
            for (int index = 0; index < order.Count; index++)
            {
                packed[order[index]] = CellAt(index, rows);
            }

            return packed;
        }

        Dictionary<string, KazoDesktopCell> positions = new(current.Positions, StringComparer.Ordinal);
        HashSet<KazoDesktopCell> taken = [.. current.Displayed.Where(pair => !movedSet.Contains(pair.Key)).Select(static pair => pair.Value)];
        // The anchor first, then the others in grid order: each keeps its distance to the anchor.
        IEnumerable<string> sequence = current.Displayed
            .Where(pair => movedSet.Contains(pair.Key))
            .OrderBy(pair => pair.Key == anchor ? -1 : Linear(pair.Value, rows))
            .Select(static pair => pair.Key);
        foreach (string key in sequence)
        {
            KazoDesktopCell from = current.Displayed[key];
            KazoDesktopCell wanted = Clamp(
                new KazoDesktopCell(target.Column + from.Column - anchorCell.Column, target.Row + from.Row - anchorCell.Row),
                columns,
                rows);
            KazoDesktopCell cell = taken.Contains(wanted) ? FirstFree(taken, columns, rows, Linear(wanted, rows) + 1) : wanted;
            taken.Add(cell);
            positions[key] = cell;
        }

        return positions;
    }

    /// <summary>Gets the cell under a point of the desktop, in CSS pixels from its top left corner.</summary>
    /// <param name="x">Horizontal offset.</param>
    /// <param name="y">Vertical offset.</param>
    /// <param name="size">The icon size, hence the cell size.</param>
    /// <param name="columns">Columns of the grid.</param>
    /// <param name="rows">Rows of the grid.</param>
    public static KazoDesktopCell CellAtPoint(double x, double y, KazoDesktopIconSize size, int columns, int rows)
    {
        (int width, int height) = CellSize(size);
        return Clamp(new KazoDesktopCell((int)Math.Floor(Math.Max(0, x) / width), (int)Math.Floor(Math.Max(0, y) / height)), columns, rows);
    }

    /// <summary>Gets how many columns and rows of cells fit in a desktop of this size (at least one of each).</summary>
    /// <param name="width">Width, in CSS pixels.</param>
    /// <param name="height">Height, in CSS pixels.</param>
    /// <param name="size">The icon size.</param>
    public static (int Columns, int Rows) GridSize(double width, double height, KazoDesktopIconSize size)
    {
        (int cellWidth, int cellHeight) = CellSize(size);
        return (Math.Max(1, (int)Math.Floor(width / cellWidth)), Math.Max(1, (int)Math.Floor(height / cellHeight)));
    }

    /// <summary>
    /// The packing order: saved items by the linear index of their cell (then the given order), new
    /// items after, in the given order.
    /// </summary>
    private static IEnumerable<string> PackingOrder(string[] keys, IReadOnlyDictionary<string, KazoDesktopCell> saved, int rows) =>
        keys
            .Select((key, index) => (key, index, has: saved.TryGetValue(key, out KazoDesktopCell cell), cell))
            .OrderBy(static entry => entry.has ? 0 : 1)
            .ThenBy(entry => entry.has ? Linear(entry.cell, rows) : 0)
            .ThenBy(static entry => entry.index)
            .Select(static entry => entry.key);

    private static bool Differs(Dictionary<string, KazoDesktopCell> positions, IReadOnlyDictionary<string, KazoDesktopCell> saved) =>
        positions.Any(pair => !saved.TryGetValue(pair.Key, out KazoDesktopCell old) || old != pair.Value);

    private static bool Inside(KazoDesktopCell cell, int columns, int rows) =>
        cell.Column >= 0 && cell.Row >= 0 && cell.Column < columns && cell.Row < rows;

    private static int Linear(KazoDesktopCell cell, int rows) => (cell.Column * rows) + cell.Row;

    private static KazoDesktopCell CellAt(int index, int rows) => new(index / rows, index % rows);

    private static KazoDesktopCell Clamp(KazoDesktopCell cell, int columns, int rows) =>
        new(Math.Clamp(cell.Column, 0, columns - 1), Math.Clamp(cell.Row, 0, rows - 1));

    /// <summary>
    /// The first free cell at or after <paramref name="start"/>, wrapping to the first cell; past a full
    /// grid, the next cells to the right (drawn off screen, never saved).
    /// </summary>
    private static KazoDesktopCell FirstFree(HashSet<KazoDesktopCell> taken, int columns, int rows, int start)
    {
        int capacity = columns * rows;
        for (int offset = 0; offset < capacity; offset++)
        {
            KazoDesktopCell cell = CellAt((start + offset) % capacity, rows);
            if (!taken.Contains(cell))
            {
                return cell;
            }
        }

        int index = capacity;
        while (taken.Contains(CellAt(index, rows)))
        {
            index++;
        }

        return CellAt(index, rows);
    }

    private static KazoDesktopCell LastFree(HashSet<KazoDesktopCell> taken, int columns, int rows)
    {
        for (int index = (columns * rows) - 1; index >= 0; index--)
        {
            KazoDesktopCell cell = CellAt(index, rows);
            if (!taken.Contains(cell))
            {
                return cell;
            }
        }

        return FirstFree(taken, columns, rows, start: 0);
    }

    private static KazoDesktopCell NearestFree(HashSet<KazoDesktopCell> taken, KazoDesktopCell wanted, int columns, int rows)
    {
        KazoDesktopCell clamped = Clamp(wanted, columns, rows);
        KazoDesktopCell? best = null;
        int bestDistance = int.MaxValue;
        for (int index = 0; index < columns * rows; index++)
        {
            KazoDesktopCell cell = CellAt(index, rows);
            if (taken.Contains(cell))
            {
                continue;
            }

            int distance = Math.Abs(cell.Column - clamped.Column) + Math.Abs(cell.Row - clamped.Row);
            if (distance < bestDistance)
            {
                best = cell;
                bestDistance = distance;
            }
        }

        return best ?? FirstFree(taken, columns, rows, start: 0);
    }

    private static string ExtensionOf(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[(dot + 1)..] : string.Empty;
    }
}
