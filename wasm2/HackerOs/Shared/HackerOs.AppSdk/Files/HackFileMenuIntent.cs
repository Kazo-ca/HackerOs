using System.Text.Json;

namespace HackerOs.AppSdk.Files;

/// <summary>
/// Reads the intent the OS hands an app for one of its file menu entries (ADR 0151 §7): the payload
/// <c>{"paths":["/home/neo/a.zip"],"scope":"File","entry":"extract"}</c> of <c>AppContext.Intent</c> or of
/// <c>OnIntentReceivedAsync</c>.
/// </summary>
/// <example>
/// <code>
/// if (LaunchIntent is { IntentId: "com.example.zip.extract.v1" } intent)
/// {
///     foreach (string path in HackFileMenuIntent.ReadPaths(intent.PayloadJson)) { … }
/// }
/// </code>
/// </example>
public static class HackFileMenuIntent
{
    /// <summary>
    /// Gets the absolute paths of the payload: the items right-clicked, or the folder shown for the
    /// empty space. A malformed payload gives no path.
    /// </summary>
    /// <param name="payloadJson">The intent's payload, or <see langword="null"/>.</param>
    public static IReadOnlyList<string> ReadPaths(string? payloadJson)
    {
        using JsonDocument? document = Parse(payloadJson);
        return document is not null
            && document.RootElement.TryGetProperty("paths", out JsonElement paths)
            && paths.ValueKind == JsonValueKind.Array
            ? [.. paths.EnumerateArray()
                .Where(static path => path.ValueKind == JsonValueKind.String)
                .Select(static path => path.GetString()!)
                .Where(static path => path.StartsWith('/'))]
            : [];
    }

    /// <summary>Gets the scope of the menu the entry was chosen in, or <see cref="FileMenuScope.None"/> when the payload has none.</summary>
    /// <param name="payloadJson">The intent's payload, or <see langword="null"/>.</param>
    public static FileMenuScope ReadScope(string? payloadJson)
    {
        using JsonDocument? document = Parse(payloadJson);
        return document is not null
            && document.RootElement.TryGetProperty("scope", out JsonElement scope)
            && scope.ValueKind == JsonValueKind.String
            && Enum.TryParse(scope.GetString(), ignoreCase: false, out FileMenuScope value)
            && Enum.IsDefined(value)
            ? value
            : FileMenuScope.None;
    }

    /// <summary>
    /// Gets the name of the entry chosen (ADR 0218 §3), so one intent can serve several entries of
    /// the app, or <see langword="null"/> when the payload has none (an OS older than ADR 0218).
    /// </summary>
    /// <param name="payloadJson">The intent's payload, or <see langword="null"/>.</param>
    public static string? ReadEntry(string? payloadJson)
    {
        using JsonDocument? document = Parse(payloadJson);
        return document is not null
            && document.RootElement.TryGetProperty("entry", out JsonElement entry)
            && entry.ValueKind == JsonValueKind.String
            ? entry.GetString()
            : null;
    }

    private static JsonDocument? Parse(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            JsonDocument document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
