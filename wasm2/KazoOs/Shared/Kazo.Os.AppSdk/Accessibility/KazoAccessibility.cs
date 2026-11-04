namespace Kazo.Os.AppSdk.Accessibility;

/// <summary>
/// The session's accessibility preferences (ADR 0105): high contrast, text size and reduced motion,
/// read from <c>/etc/hackeros/accessibility.config</c> and <c>~/.config/hackeros/accessibility.config</c>
/// with every <c>auto</c> resolved against the host's settings.
/// </summary>
/// <remarks>
/// <para>
/// The shell already applies them to everything drawn with HTML and the theme's tokens. An app that
/// draws by itself (a canvas, a terminal emulator, a code editor) reads them here and applies them:
/// a larger font, a high-contrast palette, no blinking cursor.
/// </para>
/// <para>
/// Every member targets the app running the current code (ambient context of ADR 0043); a Blazor
/// component that unsubscribes in <c>Dispose</c> uses <c>AppContext.Accessibility</c> directly.
/// No capability is needed.
/// </para>
/// </remarks>
public static class KazoAccessibility
{
    /// <summary>Raised after the effective preferences changed.</summary>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static event EventHandler? PreferencesChanged
    {
        add => Gateway.Changed += value;
        remove => Gateway.Changed -= value;
    }

    /// <summary>Gets the effective preferences.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static async Task<KazoAccessibilityPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default) =>
        await Gateway.GetPreferencesAsync(cancellationToken).ConfigureAwait(false);

    private static IAppAccessibilityGateway Gateway => KazoExecutionContext.Current.Accessibility;
}
