using System.Reflection;
using HackerOs.Simulation.Abstractions.Sounds;

namespace HackerOs.Platform.Core.Sounds;

/// <summary>
/// The sound themes HackerOS ships (ADR 0204 section 4): <c>hackeros</c> (console bleeps, the
/// default) and <c>soft</c> (chimes). <see cref="FileSystem.FileSystemSeeder"/> installs them as real
/// files in <c>/usr/share/sounds/&lt;id&gt;/</c>, like a distribution package would, so they can be
/// listed, played and copied like any user theme.
/// </summary>
/// <remarks>
/// The audio files are embedded in this assembly (<c>Sounds/Themes/&lt;id&gt;/*.oga</c>, about 5 KB each),
/// computed by <c>build/tools/generate-system-sounds.py</c>. An existing file is never replaced: an
/// administrator's edits survive every boot, as for the other seeded files.
/// </remarks>
public static class BuiltInSoundThemes
{
    private const string ResourcePrefix = "HackerOs.Sounds.";
    private const string SoundsDirectory = "stereo";

    /// <summary>Gets the built-in themes.</summary>
    public static IReadOnlyList<BuiltInSoundTheme> All { get; } =
    [
        new(OsSoundEvents.DefaultThemeId, "HackerOS", "HackerOS", "Console bleeps", "Bips de console"),
        new("soft", "Soft", "Doux", "Gentle chimes", "Carillons doux")
    ];

    /// <summary>Gets the folder of a built-in theme.</summary>
    /// <param name="id">Theme identifier.</param>
    public static string GetDirectory(string id) => $"{OsSoundEvents.SystemThemesDirectory}/{id}";

    /// <summary>Gets the path of a built-in sound file.</summary>
    /// <param name="id">Theme identifier.</param>
    /// <param name="soundEvent">The event.</param>
    public static string GetSoundPath(string id, OsSoundEvent soundEvent) =>
        $"{GetDirectory(id)}/{SoundsDirectory}/{OsSoundEvents.GetSoundName(soundEvent)}.oga";

    /// <summary>Gets the <c>index.theme</c> content of a built-in theme.</summary>
    /// <param name="theme">The theme.</param>
    public static string GetIndexContent(BuiltInSoundTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return "# Built-in HackerOS sound theme (ADR 0204). Each sound is named after a freedesktop.org\n"
            + "# sound name (dialog-error, message-new-instant...) and may be .oga, .ogg or .mp3.\n"
            + "[Sound Theme]\n"
            + $"Name={theme.Name}\n"
            + $"Name[fr]={theme.FrenchName}\n"
            + $"Comment={theme.Comment}\n"
            + $"Comment[fr]={theme.FrenchComment}\n"
            + $"Directories={SoundsDirectory}\n"
            + "\n"
            + $"[{SoundsDirectory}]\n"
            + "OutputProfile=stereo\n";
    }

    /// <summary>Reads the embedded audio of a built-in sound, or <see langword="null"/> when it is not shipped.</summary>
    /// <param name="id">Theme identifier.</param>
    /// <param name="soundEvent">The event.</param>
    public static byte[]? ReadSound(string id, OsSoundEvent soundEvent)
    {
        string wanted = $"{id}/{OsSoundEvents.GetSoundName(soundEvent)}.oga";
        Assembly assembly = typeof(BuiltInSoundThemes).Assembly;
        // The logical name holds MSBuild's RecursiveDir, with '\' when packed on Windows.
        string? name = assembly.GetManifestResourceNames().FirstOrDefault(resource =>
            resource.StartsWith(ResourcePrefix, StringComparison.Ordinal)
            && string.Equals(resource[ResourcePrefix.Length..].Replace('\\', '/'), wanted, StringComparison.Ordinal));
        if (name is null)
        {
            return null;
        }

        using Stream stream = assembly.GetManifestResourceStream(name)!;
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}

/// <summary>One built-in sound theme.</summary>
/// <param name="Id">Folder name in <c>/usr/share/sounds</c>.</param>
/// <param name="Name">English name.</param>
/// <param name="FrenchName">French name.</param>
/// <param name="Comment">English description.</param>
/// <param name="FrenchComment">French description.</param>
public sealed record BuiltInSoundTheme(string Id, string Name, string FrenchName, string Comment, string FrenchComment);
