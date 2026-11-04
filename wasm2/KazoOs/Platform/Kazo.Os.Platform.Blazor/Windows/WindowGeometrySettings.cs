using System.Globalization;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;
using Kazo.Os.Windowing.Abstractions;
using Microsoft.Extensions.Logging;

namespace Kazo.Os.Platform.Blazor.Windows;

/// <summary>
/// Locates the window geometry state file of one app:
/// <c>~/.local/state/kazoos/windows/&lt;appId&gt;.config</c>. Geometry is device-local state, not a
/// preference, so it lives outside <c>~/.config</c> and is never synchronized (ADR 0044 section 10).
/// </summary>
public static class WindowGeometrySettings
{
    /// <summary>Gets the state file of <paramref name="appId"/>.</summary>
    /// <param name="appId">Reverse-DNS app identifier.</param>
    public static OsSettingsFile GetFile(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        if (appId.Contains('/', StringComparison.Ordinal) || appId.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("An app id cannot contain path separators.", nameof(appId));
        }

        return new OsSettingsFile(OsSettingsLocation.UserState, $"windows/{appId}.config");
    }

    /// <summary>
    /// Parses a geometry file (<c>width=</c>, <c>height=</c>, invariant culture, plus the optional
    /// <c>maximized=</c>; ADR 0122). Returns <see langword="null"/> for an empty, partial or invalid
    /// file: a bad state file only means the window opens at its declared size. The <c>x=</c> and
    /// <c>y=</c> keys written before ADR 0122 are ignored: only the size is remembered, as GTK apps
    /// under GNOME do.
    /// </summary>
    /// <param name="content">File content, or <see langword="null"/> when absent.</param>
    public static SavedWindowGeometry? Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        Dictionary<string, double> values = new(StringComparer.Ordinal);
        bool maximized = false;
        foreach (ConfigLine line in ConfigDocumentLines.Parse(content))
        {
            if (line.Kind != ConfigLineKind.KeyValue || line.Group is not null || line.Key is not { } key)
            {
                continue;
            }

            if (key == "maximized")
            {
                // Anything but a literal "true" keeps the window normal: a typo never maximizes it.
                maximized = string.Equals(line.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            }
            else if (double.TryParse(line.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                && double.IsFinite(number))
            {
                values[key] = number;
            }
        }

        return values.TryGetValue("width", out double width) && width > 0
            && values.TryGetValue("height", out double height) && height > 0
            ? new SavedWindowGeometry(width, height, maximized)
            : null;
    }

    /// <summary>Serializes geometry in the format read by <see cref="Parse"/>.</summary>
    /// <param name="appId">App the geometry belongs to, named in the header comment.</param>
    /// <param name="geometry">Normal (restore) size and maximized state to save.</param>
    public static string Serialize(string appId, SavedWindowGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"# Window size of {appId} (ADR 0116, ADR 0122)\nwidth={geometry.Width}\nheight={geometry.Height}\nmaximized={(geometry.IsMaximized ? "true" : "false")}\n");
    }
}

/// <summary>
/// Geometry the window manager remembers for one app (ADR 0116 §2, ADR 0122): the size of the
/// <em>normal</em> window (never the maximized or snapped one, so a maximized window keeps its restore
/// size, as under X11) and whether the window was maximized. The position is not remembered: a window
/// always opens at its cascade slot, like a GTK app under GNOME.
/// </summary>
/// <param name="Width">Normal window width in CSS pixels.</param>
/// <param name="Height">Normal window height in CSS pixels.</param>
/// <param name="IsMaximized">Whether the window was maximized when the geometry was saved.</param>
public sealed record SavedWindowGeometry(double Width, double Height, bool IsMaximized);

/// <summary>
/// Reads and writes the window geometry state file of an app through <see cref="IOsSettingsService"/>,
/// under the identity of the signed-in user (ADR 0044 §10, ADR 0116).
/// </summary>
public sealed class WindowGeometryPersistence
{
    private readonly IOsSettingsService _settings;
    private readonly ILogger? _logger;

    /// <summary>Creates the persistence over the OS settings service.</summary>
    /// <param name="settings">Whole-file access to user settings and state files.</param>
    public WindowGeometryPersistence(IOsSettingsService settings)
        : this(settings, logger: null)
    {
    }

    /// <summary>Creates the persistence with a logger for writes that fail (geometry stays best effort).</summary>
    /// <param name="settings">Whole-file access to user settings and state files.</param>
    /// <param name="logger">Receives a warning when a state file cannot be read or written.</param>
    public WindowGeometryPersistence(IOsSettingsService settings, ILogger<WindowGeometryPersistence>? logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger;
    }

    /// <summary>Reads saved geometry, returning <see langword="null"/> for a clean profile or an unreadable file.</summary>
    /// <param name="appId">App whose window is restored.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<SavedWindowGeometry?> RestoreAsync(string appId, CancellationToken cancellationToken = default)
    {
        try
        {
            string? content = await _settings
                .ReadFileAsync(WindowGeometrySettings.GetFile(appId), cancellationToken)
                .ConfigureAwait(false);
            return WindowGeometrySettings.Parse(content);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            // No session or an unreadable file: the window opens at its cascade slot.
            _logger?.LogWarning("window geometry of {AppId} could not be read: {Error}", appId, exception.Message);
            return null;
        }
    }

    /// <summary>
    /// Saves geometry when it differs from the file's current content, so closing a window that did
    /// not move does not rewrite the file and wake file watchers (ADR 0116 §3).
    /// </summary>
    /// <param name="appId">App whose window is saved.</param>
    /// <param name="geometry">Normal bounds and maximized state.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when the file was written.</returns>
    public async ValueTask<bool> SaveAsync(
        string appId,
        SavedWindowGeometry geometry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry.Width <= 0 || geometry.Height <= 0)
        {
            return false;
        }

        try
        {
            OsSettingsFile file = WindowGeometrySettings.GetFile(appId);
            string content = WindowGeometrySettings.Serialize(appId, geometry);
            string? current = await _settings.ReadFileAsync(file, cancellationToken).ConfigureAwait(false);
            if (string.Equals(current, content, StringComparison.Ordinal))
            {
                return false;
            }

            await _settings.WriteFileAsync(file, content, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            // No session, or a home the user cannot write: geometry is best effort.
            _logger?.LogWarning("window geometry of {AppId} could not be saved: {Error}", appId, exception.Message);
            return false;
        }
    }
}
