namespace Kazo.Os.AppSdk.Apps;

/// <summary>
/// The fallback icon the shell draws for an app with no declared image (ADR 0160 §6, ADR 0232 §2): the
/// Start menu, the desktop shortcuts and the file control show the same one. A Lucide icon name, from the
/// app's id for the explorer and Settings, else from its category.
/// </summary>
public static class KazoAppIcons
{
    /// <summary>The Lucide icon of an app.</summary>
    /// <param name="appId">The app id.</param>
    /// <param name="category">Its category (<see cref="KazoAppInfo.Category"/>, lower kebab case).</param>
    /// <returns>A Lucide icon name, <c>app-window</c> when nothing more precise applies.</returns>
    public static string LucideName(string appId, string? category) => appId switch
    {
        "org.kazoos.file-explorer" => "folder",
        "org.kazoos.settings" => "settings",
        _ => (category ?? string.Empty).ToLowerInvariant() switch
        {
            "developer" => "code-2",
            "games" => "gamepad-2",
            "graphics" => "image",
            "internet" => "globe-2",
            "system" => "monitor-cog",
            "utilities" => "wrench",
            _ => "app-window"
        }
    };
}
