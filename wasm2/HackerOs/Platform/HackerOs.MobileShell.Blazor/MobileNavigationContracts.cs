namespace HackerOs.MobileShell.Blazor;

/// <summary>
/// Dispatches the three Mobile system-navigation actions (Triangle/Back, Circle/Home,
/// Square/Recent, per doc/wasm/dev/old/mobile-interface-platform-plan.md §7.2/§7.3/§7.4/§7.5). The host owns
/// how each is fulfilled — e.g. Back may consult a dialog stack, an app's declared
/// <c>IAppBackHandler</c> (<c>MOB-012</c>, not yet implemented), or a navigation stack
/// (<c>MOB-011</c>, not yet implemented) before falling back to Home. This bar only dispatches the
/// request; it never decides what Back/Home/Recent mean.
/// </summary>
public interface IMobileNavigationCommands
{
    /// <summary>Requests Back navigation, per the ordered semantics in plan §7.3.</summary>
    void RequestBack();

    /// <summary>Requests a return to the Mobile home surface, per plan §7.4.</summary>
    void RequestHome();

    /// <summary>Requests the Recent-surfaces view, per plan §7.5.</summary>
    void RequestRecent();
}

/// <summary>
/// Host-supplied labels for the navigation bar's accessible button names (mirrors
/// <c>HackerOs.Taskbar.Blazor</c>'s <c>TaskbarOptions</c>). A label left <see langword="null"/> (the
/// default) uses the package's own text, in the host's session language (ADR 0104).
/// </summary>
public sealed record MobileSystemNavigationBarOptions
{
    /// <summary>Gets the accessible label for the Back (triangle) button; "Back" by default.</summary>
    public string? BackLabel { get; init; }

    /// <summary>Gets the accessible label for the Home (circle) button; "Home" by default.</summary>
    public string? HomeLabel { get; init; }

    /// <summary>Gets the accessible label for the Recent (square) button; "Recent" by default.</summary>
    public string? RecentLabel { get; init; }
}
