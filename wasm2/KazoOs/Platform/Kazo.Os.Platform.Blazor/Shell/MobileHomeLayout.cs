using System.Text;
using System.Text.Json;
using System.Globalization;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Blazor.Shell;

/// <summary>Bounded launcher state; array order is the physical icon order on each Home page.</summary>
internal sealed class MobileHomeLayout
{
    internal const int PageCapacity = 20;
    internal const int MaximumPages = 24;
    internal const int DockCapacity = 5;
    internal List<List<string>> Pages { get; } = [];
    internal List<string> Dock { get; } = [];

    internal static MobileHomeLayout Default()
    {
        MobileHomeLayout layout = new();
        layout.Pages.Add(["org.hackeros.file-explorer", "org.hackeros.text-editor", "org.hackeros.settings"]);
        layout.Dock.AddRange(["org.hackeros.browser", "org.hackeros.terminal", "org.hackeros.file-explorer", "org.hackeros.settings"]);
        return layout;
    }

    internal MobileHomeLayout Clone()
    {
        MobileHomeLayout clone = new();
        clone.Pages.AddRange(Pages.Select(page => new List<string>(page)));
        clone.Dock.AddRange(Dock);
        return clone;
    }

    internal bool Contains(string appId) => Pages.Any(page => page.Contains(appId, StringComparer.Ordinal));

    // Reuse the OS's Linux-style configuration grammar, then enforce the bounded layout contract.
    internal static MobileHomeLayout? Decode(string? content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 131072) return null;
        ConfigDocumentParseResult parsed = ConfigDocumentFormat.Parse(content);
        if (!parsed.Success || !parsed.Values.TryGetValue("version", out string? version) || version.Trim() != "1"
            || !parsed.Values.TryGetValue("dock.apps", out string? dock)) return null;
        int pageCount = parsed.Values.Count - 2;
        if (pageCount is < 1 or > MaximumPages) return null;
        MobileHomeLayout layout = new();
        HashSet<string> placed = new(StringComparer.Ordinal);
        for (int page = 1; page <= pageCount; page++)
        {
            if (!parsed.Values.TryGetValue("pages." + page.ToString(CultureInfo.InvariantCulture), out string? apps)) return null;
            List<string>? ids = ReadIds(apps, PageCapacity);
            if (ids is null || ids.Any(id => !placed.Add(id))) return null;
            layout.Pages.Add(ids);
        }
        List<string>? dockIds = ReadIds(dock, DockCapacity);
        if (dockIds is null) return null;
        layout.Dock.AddRange(dockIds);
        return layout;
    }

    // Read-only compatibility for layouts written before ADR 0166; new saves never emit JSON.
    internal static MobileHomeLayout? DecodeLegacyJson(string? content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 131072) return null;
        try
        {
            using JsonDocument json = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Any(property => property.Name is not ("schemaVersion" or "pages" or "dock"))
                || !root.TryGetProperty("schemaVersion", out JsonElement version) || !version.TryGetInt32(out int number) || number != 1
                || !root.TryGetProperty("pages", out JsonElement pages) || pages.ValueKind != JsonValueKind.Array
                || pages.GetArrayLength() is < 1 or > MaximumPages
                || !root.TryGetProperty("dock", out JsonElement dock)) return null;
            MobileHomeLayout layout = new();
            HashSet<string> placed = new(StringComparer.Ordinal);
            foreach (JsonElement page in pages.EnumerateArray())
            {
                List<string>? ids = ReadIds(page, PageCapacity);
                if (ids is null || ids.Any(id => !placed.Add(id))) return null;
                layout.Pages.Add(ids);
            }
            List<string>? dockIds = ReadIds(dock, DockCapacity);
            if (dockIds is null) return null;
            layout.Dock.AddRange(dockIds);
            return layout;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    internal string Encode()
    {
        StringBuilder content = new("# Kazo OS mobile Home layout\nversion=1\n\n[dock]\napps=");
        content.AppendJoin(',', Dock).Append("\n\n[pages]\n");
        for (int page = 0; page < Pages.Count; page++)
        {
            content.Append((page + 1).ToString(CultureInfo.InvariantCulture)).Append('=')
                .AppendJoin(',', Pages[page]).Append('\n');
        }
        return content.ToString();
    }

    internal static bool ValidId(string id) => id.Length is > 2 and <= 200
        && id.Contains('.', StringComparison.Ordinal)
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static List<string>? ReadIds(JsonElement array, int capacity)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > capacity) return null;
        List<string> result = [];
        foreach (JsonElement value in array.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } id
                || !ValidId(id) || result.Contains(id, StringComparer.Ordinal)) return null;
            result.Add(id);
        }
        return result;
    }

    private static List<string>? ReadIds(string value, int capacity)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        List<string> ids = value.Split(',', StringSplitOptions.TrimEntries).ToList();
        return ids.Count <= capacity && ids.All(ValidId) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count
            ? ids : null;
    }
}
