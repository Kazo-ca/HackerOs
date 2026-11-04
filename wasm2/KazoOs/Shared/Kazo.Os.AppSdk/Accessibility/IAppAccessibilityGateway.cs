namespace Kazo.Os.AppSdk.Accessibility;

/// <summary>
/// Gateway behind <see cref="KazoAccessibility"/> (ADR 0105 section 3): the session's effective
/// accessibility preferences. Reading them needs no capability, as any Linux program may read the
/// desktop's accessibility settings (GNOME's <c>org.gnome.desktop.a11y.interface</c>).
/// </summary>
public interface IAppAccessibilityGateway
{
    /// <summary>Raised after the effective preferences changed (a settings file, or the host's own settings).</summary>
    event EventHandler? Changed;

    /// <summary>Gets the effective preferences, every <c>auto</c> resolved.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask<KazoAccessibilityPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The gateway of a context with no accessibility service (tests, a detached process, a Web Worker):
/// it always answers <see cref="KazoAccessibilityPreferences.Default"/> and never changes.
/// </summary>
public sealed class DefaultAppAccessibilityGateway : IAppAccessibilityGateway
{
    private DefaultAppAccessibilityGateway()
    {
    }

    /// <summary>Gets the shared instance.</summary>
    public static DefaultAppAccessibilityGateway Instance { get; } = new();

    /// <inheritdoc />
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public ValueTask<KazoAccessibilityPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(KazoAccessibilityPreferences.Default);
}
