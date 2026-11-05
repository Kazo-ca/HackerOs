namespace Kazo.Os.AppSdk.Blazor;

/// <summary>
/// The window manager's side of one app window (ADR 0117 §2): the platform hands each rendered
/// window app its own instance, bound to that window only.
/// </summary>
/// <remarks>
/// Implemented by the platform and passed to <see cref="WindowAppBase"/> as a fixed cascading
/// value, so the SDK never references <c>Kazo.Os.Windowing.*</c> (ADR 0055). Apps do not use it
/// directly: they call <see cref="WindowAppBase.SetWindowTitleAsync"/> and read
/// <see cref="WindowAppBase.WindowTitle"/>. Holding an instance only ever reaches the window it
/// was created for, never another window of the same app, so no capability guards it.
/// </remarks>
public interface IAppWindowGateway
{
    /// <summary>Gets the title the window manager currently shows for the window.</summary>
    string Title { get; }

    /// <summary>
    /// Gets the window's default title: the app's name in the desktop language (ADR 0104 §4), the
    /// one a new window gets and an empty title restores.
    /// </summary>
    string DefaultTitle { get; }

    /// <summary>
    /// Replaces the window's title. The window manager cleans it (control characters removed, white
    /// space trimmed, at most 256 characters) and shows it as given; <see langword="null"/>, empty
    /// or blank restores the app's localized name.
    /// </summary>
    /// <param name="title">The full title, e.g. <c>photo.png — Kazo Paint</c>.</param>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns>The title actually shown once cleaned.</returns>
    ValueTask<string> SetTitleAsync(string? title, CancellationToken cancellationToken = default);
}
