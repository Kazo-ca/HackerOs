namespace HackerOs.Apps.Settings;

/// <summary>
/// The pages of Settings visited, for Back and Forward (ADR 0220 §4), like a web browser's history: choosing a
/// page from the menu clears Forward. Pages that left the menu since (Backup, Application) are skipped.
/// </summary>
internal sealed class SettingsPageHistory
{
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();

    /// <summary>Records that the user chose <paramref name="next"/> while <paramref name="current"/> was shown.</summary>
    public void Visit(string current, string next)
    {
        if (current == next)
        {
            return;
        }

        _back.Push(current);
        _forward.Clear();
    }

    /// <summary>Gets the page Back would show, or <see langword="null"/>; drops entries that can't be shown.</summary>
    public string? PeekBack(string current, Func<string, bool> isVisible) => Peek(_back, current, isVisible);

    /// <summary>Gets the page Forward would show, or <see langword="null"/>; drops entries that can't be shown.</summary>
    public string? PeekForward(string current, Func<string, bool> isVisible) => Peek(_forward, current, isVisible);

    /// <summary>Goes back from <paramref name="current"/>, or returns <see langword="null"/> when there is nothing to go back to.</summary>
    public string? Back(string current, Func<string, bool> isVisible) => Move(_back, _forward, current, isVisible);

    /// <summary>Goes forward from <paramref name="current"/>, or returns <see langword="null"/> when there is nothing to go forward to.</summary>
    public string? Forward(string current, Func<string, bool> isVisible) => Move(_forward, _back, current, isVisible);

    private static string? Move(Stack<string> from, Stack<string> to, string current, Func<string, bool> isVisible)
    {
        if (Peek(from, current, isVisible) is not { } page)
        {
            return null;
        }

        from.Pop();
        to.Push(current);
        return page;
    }

    private static string? Peek(Stack<string> pages, string current, Func<string, bool> isVisible)
    {
        while (pages.TryPeek(out string? candidate))
        {
            if (candidate != current && isVisible(candidate))
            {
                return candidate;
            }

            pages.Pop();
        }

        return null;
    }
}
