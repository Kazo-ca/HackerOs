using Kazo.Os.AppSdk.Blazor.ContextMenus;

namespace Kazo.Os.Platform.Blazor.ContextMenus;

/// <summary>What a key or a pointer did to the open menu.</summary>
public enum ContextMenuNavigation
{
    /// <summary>Nothing to do outside the menu (the highlight may have moved).</summary>
    None,

    /// <summary>The whole menu must close.</summary>
    Close,

    /// <summary>The highlighted entry must run (<see cref="ContextMenuNavigator.Chosen"/>).</summary>
    Invoke
}

/// <summary>
/// The keyboard and pointer behaviour of an open menu (ADR 0150 §6), kept apart from the drawing so
/// it can be checked without a browser: a stack of open levels, each with its highlighted row.
/// </summary>
/// <remarks>
/// Up and Down move the highlight with wrap-around, skipping separators and disabled entries; Home
/// and End go to an end; Right or Enter opens a submenu on its first entry; Left closes a submenu;
/// Escape closes one level (the menu at the top level); a letter goes to the next entry starting
/// with it; Enter or Space runs the entry; Tab closes the menu, as in Windows.
/// </remarks>
public sealed class ContextMenuNavigator
{
    private readonly List<OpenLevel> _levels = [];

    /// <summary>Initializes the navigator on a menu.</summary>
    /// <param name="root">The arranged menu.</param>
    /// <param name="highlightFirst">Whether the first entry takes the highlight (a keyboard opening).</param>
    public ContextMenuNavigator(ContextMenuLevel root, bool highlightFirst)
    {
        ArgumentNullException.ThrowIfNull(root);
        _levels.Add(new OpenLevel(root, highlightFirst ? ContextMenuLayout.NextSelectable(root, -1, +1) : -1));
    }

    /// <summary>Gets the open levels, the menu first.</summary>
    public IReadOnlyList<OpenLevel> Levels => _levels;

    /// <summary>Gets the deepest open level, the one the keyboard acts on.</summary>
    public OpenLevel Active => _levels[^1];

    /// <summary>Gets the entry to run after <see cref="ContextMenuNavigation.Invoke"/>.</summary>
    public KazoMenuItemDefinition? Chosen { get; private set; }

    /// <summary>Handles a key of the browser's <c>KeyboardEvent.key</c>.</summary>
    /// <param name="key">The key.</param>
    /// <returns>What the menu must do.</returns>
    public ContextMenuNavigation HandleKey(string key)
    {
        OpenLevel active = Active;
        switch (key)
        {
            case "ArrowDown":
                active.Highlight = ContextMenuLayout.NextSelectable(active.Level, active.Highlight, +1);
                return ContextMenuNavigation.None;
            case "ArrowUp":
                active.Highlight = ContextMenuLayout.NextSelectable(
                    active.Level, active.Highlight < 0 ? active.Level.Rows.Count : active.Highlight, -1);
                return ContextMenuNavigation.None;
            case "Home":
                active.Highlight = ContextMenuLayout.NextSelectable(active.Level, -1, +1);
                return ContextMenuNavigation.None;
            case "End":
                active.Highlight = ContextMenuLayout.NextSelectable(active.Level, active.Level.Rows.Count, -1);
                return ContextMenuNavigation.None;
            case "ArrowRight":
                OpenSubmenu(_levels.Count - 1, active.Highlight, highlightFirst: true);
                return ContextMenuNavigation.None;
            case "ArrowLeft":
                if (_levels.Count > 1)
                {
                    _levels.RemoveAt(_levels.Count - 1);
                }

                return ContextMenuNavigation.None;
            case "Escape":
                if (_levels.Count == 1)
                {
                    return ContextMenuNavigation.Close;
                }

                _levels.RemoveAt(_levels.Count - 1);
                return ContextMenuNavigation.None;
            case "Tab":
                return ContextMenuNavigation.Close;
            case "Enter" or " ":
                return Activate(_levels.Count - 1, active.Highlight, highlightFirst: true);
        }

        if (key.Length == 1 && !char.IsWhiteSpace(key[0]))
        {
            int found = ContextMenuLayout.FindByLetter(active.Level, active.Highlight, key[0]);
            if (found >= 0)
            {
                active.Highlight = found;
            }
        }

        return ContextMenuNavigation.None;
    }

    /// <summary>The pointer is over a row: it takes the highlight, and its submenu opens (or deeper ones close).</summary>
    /// <param name="level">The row's level.</param>
    /// <param name="row">The row.</param>
    public void Hover(int level, int row)
    {
        if (level < 0 || level >= _levels.Count || row < 0 || row >= _levels[level].Level.Rows.Count)
        {
            return;
        }

        ContextMenuRow hovered = _levels[level].Level.Rows[row];
        _levels[level].Highlight = hovered.IsSelectable ? row : -1;
        if (hovered is { IsSelectable: true, Submenu: not null })
        {
            // Keeps the submenu (and its highlight) when it is already this row's.
            OpenSubmenu(level, row, highlightFirst: false);
        }
        else
        {
            CloseAbove(level);
        }
    }

    /// <summary>A row was clicked or tapped.</summary>
    /// <param name="level">The row's level.</param>
    /// <param name="row">The row.</param>
    /// <returns>What the menu must do.</returns>
    public ContextMenuNavigation Click(int level, int row)
    {
        if (level < 0 || level >= _levels.Count)
        {
            return ContextMenuNavigation.None;
        }

        return Activate(level, row, highlightFirst: false);
    }

    /// <summary>Closes the deepest submenu (the sheet's Back entry).</summary>
    public void Back()
    {
        if (_levels.Count > 1)
        {
            _levels.RemoveAt(_levels.Count - 1);
        }
    }

    private ContextMenuNavigation Activate(int level, int row, bool highlightFirst)
    {
        if (row < 0 || row >= _levels[level].Level.Rows.Count)
        {
            return ContextMenuNavigation.None;
        }

        ContextMenuRow target = _levels[level].Level.Rows[row];
        if (!target.IsSelectable)
        {
            return ContextMenuNavigation.None;
        }

        _levels[level].Highlight = row;
        if (target.Submenu is not null)
        {
            OpenSubmenu(level, row, highlightFirst);
            return ContextMenuNavigation.None;
        }

        Chosen = target.Item;
        return ContextMenuNavigation.Invoke;
    }

    private void OpenSubmenu(int level, int row, bool highlightFirst)
    {
        if (row < 0 || row >= _levels[level].Level.Rows.Count || _levels[level].Level.Rows[row] is not { IsSelectable: true, Submenu: { } submenu })
        {
            return;
        }

        if (_levels.Count > level + 1 && _levels[level + 1].ParentRow == row)
        {
            if (highlightFirst && _levels[level + 1].Highlight < 0)
            {
                _levels[level + 1].Highlight = ContextMenuLayout.NextSelectable(submenu, -1, +1);
            }

            CloseAbove(level + 1);
            return;
        }

        CloseAbove(level);
        _levels.Add(new OpenLevel(submenu, highlightFirst ? ContextMenuLayout.NextSelectable(submenu, -1, +1) : -1) { ParentRow = row });
    }

    private void CloseAbove(int level)
    {
        if (_levels.Count > level + 1)
        {
            _levels.RemoveRange(level + 1, _levels.Count - level - 1);
        }
    }

    /// <summary>One open level and its highlighted row.</summary>
    /// <param name="level">The level.</param>
    /// <param name="highlight">The highlighted row, or -1.</param>
    public sealed class OpenLevel(ContextMenuLevel level, int highlight)
    {
        /// <summary>Gets the level.</summary>
        public ContextMenuLevel Level { get; } = level;

        /// <summary>Gets or sets the highlighted row, or -1.</summary>
        public int Highlight { get; set; } = highlight;

        /// <summary>Gets the row of the parent level that opened this one, or -1 for the menu.</summary>
        public int ParentRow { get; init; } = -1;
    }
}
