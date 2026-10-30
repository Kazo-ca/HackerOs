namespace HackerOs.Simulation.Abstractions.Sounds;

/// <summary>
/// An event of the OS that plays a sound of the chosen sound theme (ADR 0204 section 2).
/// </summary>
public enum OsSoundEvent
{
    /// <summary>A notification was posted (information or success).</summary>
    Notification,

    /// <summary>A warning: a warning notification or a message box with the warning icon.</summary>
    Warning,

    /// <summary>An error: an error notification or a message box with the error icon.</summary>
    Error,

    /// <summary>A session opened (sign-in).</summary>
    SignIn,

    /// <summary>A session closed (sign-out).</summary>
    SignOut
}

/// <summary>
/// Names and limits of the sound themes, after the freedesktop.org sound theme and naming
/// specifications (ADR 0204 section 1): a theme is a folder with an <c>index.theme</c> file and one
/// audio file per event, named after the event (<c>dialog-error.oga</c>).
/// </summary>
public static class OsSoundEvents
{
    /// <summary>Folder of the themes installed for every user (an administrator's folder).</summary>
    public const string SystemThemesDirectory = "/usr/share/sounds";

    /// <summary>Folder of a user's own themes, under the home folder.</summary>
    public const string UserThemesRelativeDirectory = ".local/share/sounds";

    /// <summary>Name of the description file of a theme.</summary>
    public const string IndexFileName = "index.theme";

    /// <summary>Identifier of the default theme, used for an event a theme does not provide.</summary>
    public const string DefaultThemeId = "hackeros";

    /// <summary>Largest sound file played, in bytes (1 MiB, ADR 0190 section 3).</summary>
    public const long MaxSoundFileBytes = 1024 * 1024;

    /// <summary>Gets every event, in the order Settings lists them.</summary>
    public static IReadOnlyList<OsSoundEvent> All { get; } =
        [OsSoundEvent.Notification, OsSoundEvent.Warning, OsSoundEvent.Error, OsSoundEvent.SignIn, OsSoundEvent.SignOut];

    /// <summary>Gets the accepted file extensions, in the order they are looked for.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".oga", ".ogg", ".mp3"];

    /// <summary>Gets the freedesktop.org sound name of an event, the file name without its extension.</summary>
    /// <param name="soundEvent">The event.</param>
    public static string GetSoundName(OsSoundEvent soundEvent) => soundEvent switch
    {
        OsSoundEvent.Notification => "message-new-instant",
        OsSoundEvent.Warning => "dialog-warning",
        OsSoundEvent.Error => "dialog-error",
        OsSoundEvent.SignIn => "desktop-login",
        OsSoundEvent.SignOut => "desktop-logout",
        _ => throw new ArgumentOutOfRangeException(nameof(soundEvent), soundEvent, null)
    };

    /// <summary>Gets the media type of a sound file from its extension, or <see langword="null"/> when not accepted.</summary>
    /// <param name="fileName">File name or path.</param>
    public static string? GetMediaType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".oga" or ".ogg" => "audio/ogg",
        ".mp3" => "audio/mpeg",
        _ => null
    };

    /// <summary>Gets the folder of a user's own themes.</summary>
    /// <param name="homePath">The user's home folder (<c>/home/alice</c>).</param>
    public static string GetUserThemesDirectory(string homePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homePath);
        return homePath.TrimEnd('/') + "/" + UserThemesRelativeDirectory;
    }

    /// <summary>
    /// Checks a theme identifier (its folder name): lowercase letters, digits, <c>-</c>, <c>_</c> and
    /// <c>.</c>, 1 to 64 characters, not starting with a dot.
    /// </summary>
    /// <param name="id">The identifier.</param>
    public static bool IsValidThemeId(string? id) =>
        !string.IsNullOrEmpty(id)
        && id.Length <= 64
        && id[0] != '.'
        && id.All(static c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.');
}
