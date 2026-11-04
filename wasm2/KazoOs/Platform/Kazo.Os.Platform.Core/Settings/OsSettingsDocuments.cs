using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Settings;

/// <summary>
/// One layered OS settings document (ADR 0044 section 8): <c>/etc/hackeros/&lt;id&gt;.config</c> as the
/// system layer and <c>~/.config/hackeros/&lt;id&gt;.config</c> as the user layer.
/// </summary>
/// <param name="DocumentId">File name without <c>.config</c>.</param>
/// <param name="Schema">Declared keys, types and defaults.</param>
/// <param name="SystemOnly">
/// When <see langword="true"/> the user layer is never read nor written (for example the
/// administrator policy): only <c>/etc/hackeros</c> applies.
/// </param>
/// <remarks>
/// OS documents hold no sensitive keys (ADR 0100): <see cref="OsSettingsService"/> reads only the
/// public files, so a schema with a non-<see cref="SettingSensitivity.Public"/> field is refused here
/// rather than having its values silently ignored.
/// </remarks>
public sealed record OsSettingsDocument(string DocumentId, SettingsSchema Schema, bool SystemOnly = false)
{
    /// <summary>Gets the declared schema; every field is public.</summary>
    public SettingsSchema Schema { get; init; } = SettingsPrivateFiles.HasPrivateFields(Schema)
        ? throw new ArgumentException(
            $"OS settings document '{DocumentId}' declares sensitive keys; OS documents hold public keys only (ADR 0100).",
            nameof(Schema))
        : Schema;

    /// <summary>Directory of every OS system-layer file.</summary>
    public const string SystemDirectory = "/etc/hackeros";

    /// <summary>Gets the system layer path, <c>/etc/hackeros/&lt;id&gt;.config</c>.</summary>
    public VirtualPath SystemPath => VirtualPath.Parse($"{SystemDirectory}/{DocumentId}.config");

    /// <summary>Gets the user layer path for one home directory.</summary>
    /// <param name="homeDirectory">Absolute home directory, e.g. <c>/home/alice</c>.</param>
    public VirtualPath GetUserPath(string homeDirectory) =>
        VirtualPath.Parse($"{homeDirectory.TrimEnd('/')}/.config/hackeros/{DocumentId}.config");
}

/// <summary>Where a whole-file OS document lives.</summary>
public enum OsSettingsLocation
{
    /// <summary><c>/etc/hackeros/</c>: machine-wide, written by an administrator.</summary>
    System = 1,

    /// <summary><c>~/.config/hackeros/</c>: the session user's own preferences, synchronized with the home.</summary>
    UserConfig = 2,

    /// <summary><c>~/.local/state/hackeros/</c>: device-local user state, never synchronized (ADR 0044 section 10).</summary>
    UserState = 3,

    /// <summary>The session user's home, for documents whose location is defined relative to it.</summary>
    UserHome = 4
}

/// <summary>
/// One whole-file OS document that is not layered, such as mobile Home layout or window geometry.
/// </summary>
/// <param name="Location">Root directory the file lives under.</param>
/// <param name="RelativePath">Path below that root, e.g. <c>file-associations.json</c> or <c>windows/org.x.y.config</c>.</param>
public sealed record OsSettingsFile(OsSettingsLocation Location, string RelativePath)
{
    /// <summary>Resolves the absolute path for a session user.</summary>
    /// <param name="homeDirectory">Session home directory; required for user locations.</param>
    /// <exception cref="InvalidOperationException">A user location is resolved with no session.</exception>
    public VirtualPath Resolve(string? homeDirectory)
    {
        string root = Location switch
        {
            OsSettingsLocation.System => OsSettingsDocument.SystemDirectory,
            OsSettingsLocation.UserConfig => $"{RequireHome(homeDirectory)}/.config/hackeros",
            OsSettingsLocation.UserState => $"{RequireHome(homeDirectory)}/.local/state/hackeros",
            OsSettingsLocation.UserHome => RequireHome(homeDirectory),
            _ => throw new ArgumentOutOfRangeException(nameof(Location), Location, "Unknown OS settings location.")
        };
        return VirtualPath.Parse($"{root}/{RelativePath.TrimStart('/')}");
    }

    private static string RequireHome(string? homeDirectory) =>
        string.IsNullOrWhiteSpace(homeDirectory)
            ? throw new InvalidOperationException("A user settings file needs an active session.")
            : homeDirectory.TrimEnd('/');
}

/// <summary>Describes which OS settings document changed.</summary>
public sealed class OsSettingsChangedEventArgs : EventArgs
{
    /// <summary>Creates the event arguments.</summary>
    /// <param name="documentId">
    /// The document or file that changed, or <see langword="null"/> when every user layer changed at
    /// once because a session started or ended.
    /// </param>
    public OsSettingsChangedEventArgs(string? documentId) => DocumentId = documentId;

    /// <summary>Gets the changed document id (a <see cref="OsSettingsDocument.DocumentId"/> or an <see cref="OsSettingsFile.RelativePath"/>), or <see langword="null"/> for all.</summary>
    public string? DocumentId { get; }

    /// <summary>Gets whether this change concerns <paramref name="documentId"/>.</summary>
    /// <param name="documentId">Document id or relative file path to test.</param>
    public bool Concerns(string documentId) => DocumentId is null || DocumentId == documentId;
}
