using System.Text.Json.Serialization;

namespace Kazo.Os.App.Abstractions;

/// <summary>
/// Controls whether the launcher/start menu offers an application for direct user launch.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppLaunchVisibility>))]
public enum AppLaunchVisibility
{
    /// <summary>Shown in the launcher, start menu, and search.</summary>
    [JsonStringEnumMemberName("visible")]
    Visible = 1,

    /// <summary>Never listed for direct launch; only reachable through intents/dependencies.</summary>
    [JsonStringEnumMemberName("hidden")]
    Hidden = 2
}

/// <summary>
/// Declares how an application presents itself in shell surfaces.
/// </summary>
/// <param name="Category">Stable lower-kebab-case category identifier used for grouping.</param>
/// <param name="LaunchVisibility">Whether the app is directly launchable from shell surfaces.</param>
/// <param name="IconAssetPaths">Package-relative icon asset paths declared in <see cref="AppManifest.Assets"/>, ordered from smallest to largest.</param>
public sealed record PresentationManifest(
    string Category,
    AppLaunchVisibility LaunchVisibility,
    IReadOnlyList<string> IconAssetPaths);

/// <summary>
/// Maps one normalized culture name to a package-relative localized label/help resource file.
/// </summary>
/// <param name="Culture">Normalized BCP 47 culture name, e.g. <c>fr-FR</c>.</param>
/// <param name="ResourcePath">Package-relative path to the localization JSON resource file.</param>
public sealed record LocalizationManifest(string Culture, string ResourcePath);
