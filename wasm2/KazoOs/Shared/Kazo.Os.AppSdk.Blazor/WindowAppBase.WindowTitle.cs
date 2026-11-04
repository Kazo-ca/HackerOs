using Kazo.Os.AppSdk.Blazor.Display;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.AppSdk.Blazor;

/// <summary>
/// Window title of a window app (ADR 0117): the app writes its whole title, as an X11 client writes
/// <c>_NET_WM_NAME</c>, and the window manager shows it in the title bar, the taskbar and the
/// window switcher.
/// </summary>
public abstract partial class WindowAppBase
{
    private readonly HashSet<DisplaySurfaceTracking> _trackedSurfaces = [];
    private string? _detachedTitle;
    private string? _appTitle;
    private string? _lastRequestedTitle;
    private bool _titleRequested;

    /// <summary>
    /// Gets or sets the platform's handle on this app's window. Supplied by the window host as a
    /// fixed cascading value before the first lifecycle hook runs, so a title set in
    /// <see cref="OnAppInitialized"/> is shown before the window is first painted.
    /// </summary>
    [CascadingParameter]
    private IAppWindowGateway? WindowGateway { get; set; }

    /// <summary>
    /// Gets the title the window manager shows for this window (title bar, taskbar, Alt+`): the app's
    /// localized name until <see cref="SetWindowTitleAsync"/> changes it.
    /// </summary>
    protected string WindowTitle => WindowGateway?.Title ?? _detachedTitle ?? AppContext?.Manifest.Name ?? string.Empty;

    /// <summary>
    /// Builds a title in the base apps' convention (ADR 0117 §1): <c>document — App</c>, with
    /// <c>*</c> in front while the document has unsaved changes, where <c>App</c> is the app's name
    /// in the desktop language. Without a document, the app's name alone.
    /// </summary>
    /// <param name="document">What the window shows: a file name, a folder name; or <see langword="null"/>.</param>
    /// <param name="modified">Whether the document has unsaved changes.</param>
    /// <returns>The title to pass to <see cref="SetWindowTitleAsync"/>.</returns>
    protected string FormatWindowTitle(string? document, bool modified = false)
    {
        string appName = WindowGateway?.DefaultTitle ?? AppContext?.Manifest.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(document))
        {
            return appName;
        }

        return $"{(modified ? "*" : string.Empty)}{document.Trim()} — {appName}";
    }

    /// <summary>
    /// Replaces this window's title (ADR 0117 §2). The app gives the whole title; the convention of
    /// the base apps is <c>document — App</c>, with <c>*</c> in front while the document has unsaved
    /// changes (<c>*photo.png — Kazo Paint</c>). <see langword="null"/>, empty or blank restores the
    /// app's localized name. Only this app instance's window changes, never another window of the
    /// same app, and no capability is needed. Detached surfaces followed with
    /// <see cref="TrackDisplaySurface"/> take the same title on their browser window.
    /// </summary>
    /// <param name="title">The full title.</param>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns>A task completed once the window manager shows the new title.</returns>
    /// <remarks>
    /// The window manager cleans the title before showing it: control characters removed, white space
    /// trimmed, at most 256 characters (cut with <c>…</c>). Call it from
    /// <see cref="OnAppInitialized"/> or <see cref="OnAppInitializedAsync"/> to name the window before
    /// it is first shown. Setting the title it already has does nothing.
    /// </remarks>
    protected async ValueTask SetWindowTitleAsync(string? title, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (AppContext is null)
        {
            throw new InvalidOperationException("The window host must provide AppContext.");
        }

        // Apps typically call this after every render with the same title: skip the round trip.
        if (_titleRequested && string.Equals(title, _lastRequestedTitle, StringComparison.Ordinal))
        {
            return;
        }

        string shown;
        if (WindowGateway is { } gateway)
        {
            shown = await gateway.SetTitleAsync(title, cancellationToken);
        }
        else
        {
            // No window manager (tests, prerendering): keep the title so WindowTitle still reads it.
            shown = string.IsNullOrWhiteSpace(title) ? AppContext.Manifest.Name : title.Trim();
            _detachedTitle = shown;
        }

        _titleRequested = true;
        _lastRequestedTitle = title;
        _appTitle = string.IsNullOrWhiteSpace(title) ? null : shown;
        foreach (DisplaySurfaceTracking tracking in _trackedSurfaces.ToArray())
        {
            await tracking.ApplyTitleAsync(shown);
        }
    }

    /// <summary>Starts or stops giving a followed surface the app's title.</summary>
    private void OnSurfaceTrackingChanged(DisplaySurfaceTracking tracking, bool tracked)
    {
        if (!tracked)
        {
            _trackedSurfaces.Remove(tracking);
            return;
        }

        _trackedSurfaces.Add(tracking);
        if (_appTitle is { } title)
        {
            _ = tracking.ApplyTitleAsync(title);
        }
    }

    private void LogSurfaceTitleFailure(Exception exception)
    {
        try
        {
            AppContext?.Logging.Log(
                DiagnosticSeverity.Warning,
                $"window: detached surface title failed: {exception.GetType().Name}: {exception.Message}",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["hook"] = nameof(SetWindowTitleAsync) });
        }
        catch (Exception)
        {
            // Logging is best effort.
        }
    }
}
