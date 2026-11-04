namespace Kazo.Os.AppSdk.Terminal;

/// <summary>
/// The tabs of a console and which one is active (ADR 0058, section 4), kept apart from
/// <see cref="TerminalTabs"/> so the ordering rules are testable without a renderer: a new tab
/// opens right after the active one, closing the active tab activates its right neighbour (else
/// its left one), and switching wraps around, as in gnome-terminal.
/// </summary>
public sealed class TerminalTabSet
{
    private readonly List<TerminalTabState> _tabs = [];
    private int _nextId;

    /// <summary>Gets the open tabs, in display order.</summary>
    public IReadOnlyList<TerminalTabState> Tabs => _tabs;

    /// <summary>Gets the active tab, or <see langword="null"/> once every tab is closed.</summary>
    public TerminalTabState? Active { get; private set; }

    /// <summary>Opens a tab right after the active one and activates it.</summary>
    /// <param name="initialCwd">Starting directory of the tab's shell; <see langword="null"/> for the host's.</param>
    /// <returns>The new tab.</returns>
    public TerminalTabState Open(string? initialCwd)
    {
        TerminalTabState tab = new($"tab-{++_nextId}", initialCwd);
        int insertAt = Active is null ? _tabs.Count : _tabs.IndexOf(Active) + 1;
        _tabs.Insert(insertAt, tab);
        Active = tab;
        return tab;
    }

    /// <summary>Activates <paramref name="tab"/> if it is still open.</summary>
    public void Activate(TerminalTabState tab)
    {
        if (_tabs.Contains(tab))
        {
            Active = tab;
        }
    }

    /// <summary>Activates the tab <paramref name="offset"/> positions away, wrapping around.</summary>
    /// <param name="offset"><c>-1</c> for the previous tab, <c>+1</c> for the next one.</param>
    /// <returns>Whether the active tab changed.</returns>
    public bool Switch(int offset)
    {
        if (Active is null || _tabs.Count < 2)
        {
            return false;
        }

        int index = (_tabs.IndexOf(Active) + offset) % _tabs.Count;
        Active = _tabs[index < 0 ? index + _tabs.Count : index];
        return true;
    }

    /// <summary>
    /// Closes <paramref name="tab"/>. When it was active, its right neighbour (else its left one)
    /// becomes active.
    /// </summary>
    /// <returns>Whether the tab was open.</returns>
    public bool Close(TerminalTabState tab)
    {
        int index = _tabs.IndexOf(tab);
        if (index < 0)
        {
            return false;
        }

        _tabs.RemoveAt(index);
        if (_tabs.Count == 0)
        {
            Active = null;
        }
        else if (Active == tab)
        {
            Active = _tabs[Math.Min(index, _tabs.Count - 1)];
        }

        return true;
    }
}

/// <summary>One console tab: its panel (hence its shell) and the title the panel reports.</summary>
/// <param name="id">Stable key of the tab for rendering.</param>
/// <param name="initialCwd">Starting directory of the tab's shell.</param>
public sealed class TerminalTabState(string id, string? initialCwd)
{
    /// <summary>Gets the stable key of the tab.</summary>
    public string Id { get; } = id;

    /// <summary>Gets the directory the tab's shell started in.</summary>
    public string? InitialCwd { get; } = initialCwd;

    /// <summary>Gets or sets the tab's title: running command, else the directory's name.</summary>
    public string Title { get; set; } = "~";

    /// <summary>Gets or sets the rendered panel, once Blazor has created it.</summary>
    public TerminalPanel? Panel { get; set; }
}
