using System.Text.Json;
using System.Text.Json.Nodes;
using HackerOs.AppSdk.IO;

namespace HackerOs.Apps.FileExplorer;

/// <summary>
/// The File Explorer's own settings, a real file of the user (<c>~/.config/hackeros/file-explorer.json</c>,
/// like every setting of the OS): for now the width of the Places panel (ADR 0185 §1). Keys the explorer
/// doesn't know are kept when it writes.
/// </summary>
internal static class FileExplorerSettings
{
    /// <summary>The Places panel's width before any resize, and after a double click on its handle.</summary>
    internal const int DefaultSidebarWidth = 150;

    /// <summary>The narrowest the Places panel gets.</summary>
    internal const int MinSidebarWidth = 140;

    /// <summary>The widest the Places panel gets.</summary>
    internal const int MaxSidebarWidth = 400;

    private const string SidebarWidthKey = "sidebarWidth";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Gets the settings file of the user whose home is <paramref name="homeDirectory"/>.</summary>
    internal static string GetPath(string homeDirectory) => homeDirectory.TrimEnd('/') + "/.config/hackeros/file-explorer.json";

    /// <summary>Keeps a width between <see cref="MinSidebarWidth"/> and <see cref="MaxSidebarWidth"/>.</summary>
    internal static int ClampSidebarWidth(int width) => Math.Clamp(width, MinSidebarWidth, MaxSidebarWidth);

    /// <summary>Reads the panel's width from the file's content: the default when absent, damaged or not a number.</summary>
    internal static int ParseSidebarWidth(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return DefaultSidebarWidth;
        }

        try
        {
            return JsonNode.Parse(content) is JsonObject settings
                && settings[SidebarWidthKey] is JsonValue value
                && value.TryGetValue(out int width)
                ? ClampSidebarWidth(width)
                : DefaultSidebarWidth;
        }
        catch (JsonException)
        {
            return DefaultSidebarWidth;
        }
    }

    /// <summary>Returns the file's content with the panel's width set, the other keys unchanged.</summary>
    internal static string WithSidebarWidth(string? content, int width)
    {
        JsonObject settings;
        try
        {
            settings = string.IsNullOrWhiteSpace(content) ? [] : JsonNode.Parse(content) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            settings = [];
        }

        settings[SidebarWidthKey] = ClampSidebarWidth(width);
        return settings.ToJsonString(Indented);
    }

    /// <summary>Reads the panel's width of the user whose home is <paramref name="homeDirectory"/>.</summary>
    internal static async Task<int> ReadSidebarWidthAsync(string homeDirectory, CancellationToken cancellationToken)
    {
        string path = GetPath(homeDirectory);
        return await HackFile.ExistsAsync(path, cancellationToken)
            ? ParseSidebarWidth(await HackFile.ReadAllTextAsync(path, cancellationToken))
            : DefaultSidebarWidth;
    }

    /// <summary>Saves the panel's width, creating <c>~/.config/hackeros</c> when needed.</summary>
    internal static async Task SaveSidebarWidthAsync(string homeDirectory, int width, CancellationToken cancellationToken)
    {
        string path = GetPath(homeDirectory);
        string directory = path[..path.LastIndexOf('/')];
        if (!await HackDirectory.ExistsAsync(directory, cancellationToken))
        {
            await HackDirectory.CreateDirectoryAsync(directory, cancellationToken);
        }

        string? content = await HackFile.ExistsAsync(path, cancellationToken)
            ? await HackFile.ReadAllTextAsync(path, cancellationToken)
            : null;
        await HackFile.WriteAllTextAsync(path, WithSidebarWidth(content, width), cancellationToken);
    }
}
