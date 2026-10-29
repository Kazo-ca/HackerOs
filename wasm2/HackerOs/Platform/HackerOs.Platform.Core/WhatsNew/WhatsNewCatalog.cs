using System.Globalization;
using System.Text.Json;
using HackerOs.AppSdk.WhatsNew;

namespace HackerOs.Platform.Core.WhatsNew;

/// <summary>Reads the <c>whats-new.json</c> the origin serves (ADR 0186 §4.1).</summary>
public interface IWhatsNewSource
{
    /// <summary>Gets the content of the served <c>whats-new.json</c>, or <see langword="null"/> when there is none or it is unreachable.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask<string?> GetDocumentAsync(CancellationToken cancellationToken);
}

/// <summary>One entry of <c>whats-new.json</c>, with its texts in every language it has.</summary>
/// <param name="Id">The entry's id (a lowercase GUID).</param>
/// <param name="Category">Its category.</param>
/// <param name="Title">Its title by language (<c>en</c>, <c>fr</c>).</param>
/// <param name="Description">Its description by language; empty when it has none.</param>
/// <param name="PullRequestUrl">Its pull request, when known.</param>
/// <param name="Links">Pages that tell more.</param>
public sealed record WhatsNewCatalogEntry(
    string Id,
    HackChangeCategory Category,
    IReadOnlyDictionary<string, string> Title,
    IReadOnlyDictionary<string, string> Description,
    Uri? PullRequestUrl,
    IReadOnlyList<Uri> Links)
{
    /// <summary>The entry in <paramref name="culture"/>'s language, English when it has no text in it.</summary>
    /// <param name="culture">The caller's language.</param>
    /// <param name="isNew">Whether the user has not seen it.</param>
    public HackChangeEntry Localize(CultureInfo culture, bool isNew) =>
        new(Id, Category, Pick(Title, culture) ?? Id, Pick(Description, culture), PullRequestUrl, Links, isNew);

    private static string? Pick(IReadOnlyDictionary<string, string> texts, CultureInfo culture) =>
        texts.TryGetValue(culture.TwoLetterISOLanguageName, out string? text)
            || texts.TryGetValue("en", out text)
            ? text
            : texts.Values.FirstOrDefault();
}

/// <summary>One group of <c>whats-new.json</c>: a published version, or the next one (<see cref="Version"/> is <see langword="null"/>).</summary>
/// <param name="Version">The published version, or <see langword="null"/> for the next one.</param>
/// <param name="Since">For the next version, the version it follows.</param>
/// <param name="Date">The release date, when known.</param>
/// <param name="Entries">Its entries, in display order.</param>
public sealed record WhatsNewCatalogGroup(string? Version, string? Since, DateOnly? Date, IReadOnlyList<WhatsNewCatalogEntry> Entries);

/// <summary>
/// The change list the OS shows (ADR 0186 §4.1): the user entries of every version, grouped by
/// version, the most recent first, as <c>Tools/HackerOs.Tools.ChangeList</c> writes them.
/// </summary>
/// <param name="Groups">The groups, the most recent first.</param>
public sealed record WhatsNewCatalog(IReadOnlyList<WhatsNewCatalogGroup> Groups)
{
    /// <summary>Gets an empty change list.</summary>
    public static WhatsNewCatalog Empty { get; } = new([]);

    /// <summary>Gets every entry, the most recent group first.</summary>
    public IEnumerable<WhatsNewCatalogEntry> Entries => Groups.SelectMany(static group => group.Entries);

    /// <summary>
    /// Gets the ids a new account sees as new (ADR 0186 §5): the entries of the latest published
    /// version and those of the next version already deployed.
    /// </summary>
    public IReadOnlySet<string> LatestIds
    {
        get
        {
            HashSet<string> ids = new(StringComparer.Ordinal);
            foreach (WhatsNewCatalogGroup group in Groups.Where(static group => group.Version is null)
                .Concat(Groups.Where(static group => group.Version is not null).Take(1)))
            {
                ids.UnionWith(group.Entries.Select(static entry => entry.Id));
            }

            return ids;
        }
    }

    /// <summary>
    /// Reads a <c>whats-new.json</c> document. A missing or malformed document is an empty list; a
    /// malformed group or entry is skipped, so one bad entry never hides the others.
    /// </summary>
    /// <param name="document">The JSON text.</param>
    public static WhatsNewCatalog Parse(string? document)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            return Empty;
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(document);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("groups", out JsonElement groups)
                || groups.ValueKind != JsonValueKind.Array)
            {
                return Empty;
            }

            HashSet<string> seenIds = new(StringComparer.Ordinal);
            List<WhatsNewCatalogGroup> result = [];
            foreach (JsonElement group in groups.EnumerateArray().Where(static group => group.ValueKind == JsonValueKind.Object))
            {
                List<WhatsNewCatalogEntry> entries = [];
                if (group.TryGetProperty("entries", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in items.EnumerateArray())
                    {
                        // An id is listed once, in its most recent group.
                        if (ReadEntry(item) is { } entry && seenIds.Add(entry.Id))
                        {
                            entries.Add(entry);
                        }
                    }
                }

                if (entries.Count > 0)
                {
                    result.Add(new WhatsNewCatalogGroup(
                        String(group, "version"),
                        String(group, "since"),
                        DateOnly.TryParseExact(String(group, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date) ? date : null,
                        entries));
                }
            }

            return new WhatsNewCatalog(result);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static WhatsNewCatalogEntry? ReadEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || String(item, "id") is not { } id
            || ParseCategory(String(item, "category")) is not { } category)
        {
            return null;
        }

        IReadOnlyDictionary<string, string> title = Texts(item, "title");
        if (title.Count == 0)
        {
            return null;
        }

        List<Uri> links = [];
        if (item.TryGetProperty("links", out JsonElement linkItems) && linkItems.ValueKind == JsonValueKind.Array)
        {
            links.AddRange(linkItems.EnumerateArray()
                .Where(static link => link.ValueKind == JsonValueKind.String)
                .Select(static link => HttpsUri(link.GetString()))
                .OfType<Uri>());
        }

        return new WhatsNewCatalogEntry(id, category, title, Texts(item, "description"), HttpsUri(String(item, "prUrl")), links);
    }

    private static HackChangeCategory? ParseCategory(string? category) => category switch
    {
        "new" => HackChangeCategory.New,
        "improved" => HackChangeCategory.Improved,
        "fixed" => HackChangeCategory.Fixed,
        "developer" => HackChangeCategory.Developer,
        _ => null
    };

    // Only https links are shown: the file comes from the origin, but a link opens outside the OS.
    private static Uri? HttpsUri(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static IReadOnlyDictionary<string, string> Texts(JsonElement element, string name)
    {
        Dictionary<string, string> texts = new(StringComparer.Ordinal);
        if (element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty text in value.EnumerateObject())
            {
                if (text.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.Value.GetString()))
                {
                    texts[text.Name] = text.Value.GetString()!;
                }
            }
        }

        return texts;
    }
}
