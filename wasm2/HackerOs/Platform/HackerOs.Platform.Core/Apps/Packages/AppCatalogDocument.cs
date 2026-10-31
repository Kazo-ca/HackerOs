using System.Globalization;
using System.Text.Json;
using HackerOs.App.Abstractions;

namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>What a catalog entry installs (<c>kind</c> of <c>catalog.json</c>).</summary>
public enum AppCatalogEntryKind
{
    /// <summary><c>app</c>: a <c>.hackapp</c> package (ADR 0182).</summary>
    App = 0,

    /// <summary><c>theme-pack</c>: a <c>.hacktheme</c> pack, data only (ADR 0190, ADR 0213).</summary>
    ThemePack = 1
}

/// <summary>A category of a catalog, with its label per language (<c>categories.json</c> of the catalog repository).</summary>
/// <param name="Id">Category ID, such as <c>productivity</c>.</param>
/// <param name="Labels">Label per language code; English and French are always present.</param>
public sealed record AppCatalogCategory(string Id, IReadOnlyDictionary<string, string> Labels);

/// <summary>One published version of an app of a catalog (ADR 0188 §6).</summary>
/// <param name="Version">The version.</param>
/// <param name="Commit">Commit of the app repository the catalog compiled.</param>
/// <param name="Package">Path of the <c>.hackapp</c>, relative to the catalog root.</param>
/// <param name="Sha256">Lower-case hexadecimal SHA-256 of the package.</param>
/// <param name="Size">Size of the package in bytes.</param>
/// <param name="Capabilities">Capabilities this version declares.</param>
public sealed record AppCatalogVersion(
    SemanticVersion Version,
    string Commit,
    string Package,
    string Sha256,
    long Size,
    IReadOnlyList<string> Capabilities)
{
    /// <summary>Gets when the catalog published it, if it says.</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>Gets the oldest SDK it runs on, if it says.</summary>
    public string? MinimumSdkVersion { get; init; }

    /// <summary>Gets the newest SDK it runs on, if it says.</summary>
    public string? MaximumSdkVersion { get; init; }

    /// <summary>Gets the path of its 256×256 PNG icon, relative to the catalog root.</summary>
    public string? Icon { get; init; }

    /// <summary>Gets the paths of its screenshots, relative to the catalog root.</summary>
    public IReadOnlyList<string> Screenshots { get; init; } = [];

    /// <summary>Gets the path of its long Markdown description per language, relative to the catalog root.</summary>
    public IReadOnlyDictionary<string, string> Descriptions { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>An app of a catalog, with what the Hack Store shows on its page (ADR 0188 §2, §6).</summary>
/// <param name="AppId">App identifier.</param>
/// <param name="Name">Name of its newest version.</param>
/// <param name="Category">Category ID.</param>
/// <param name="Tags">Free tags.</param>
/// <param name="ShortDescriptions">Short description per language; English is always present.</param>
/// <param name="License">SPDX license expression.</param>
/// <param name="Repository">Its source code repository.</param>
/// <param name="LatestVersion">The newest published version.</param>
/// <param name="Versions">Published versions, newest first.</param>
public sealed record AppCatalogEntry(
    string AppId,
    string Name,
    string Category,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> ShortDescriptions,
    string License,
    string Repository,
    SemanticVersion LatestVersion,
    IReadOnlyList<AppCatalogVersion> Versions)
{
    /// <summary>Gets what it installs; <see cref="AppCatalogEntryKind.App"/> unless the catalog says <c>theme-pack</c>.</summary>
    public AppCatalogEntryKind Kind { get; init; }

    /// <summary>Gets the version <see cref="LatestVersion"/> names, or the newest one.</summary>
    public AppCatalogVersion Latest => Versions.FirstOrDefault(entry => entry.Version == LatestVersion) ?? Versions[0];

    /// <summary>
    /// Gets the versions as an <see cref="AppPackageIndex"/>, so a catalog app is installed, checked
    /// and updated exactly like a repository's (ADR 0212 §1).
    /// </summary>
    public AppPackageIndex ToIndex() =>
        new(AppId, [.. Versions.Select(static version => new AppPackageIndexEntry(version.Version, version.Package, version.Sha256))]);
}

/// <summary>A downloaded <c>catalog.json</c> (ADR 0188 §6), with the address its paths are relative to.</summary>
/// <param name="Name">Catalog name in <c>[catalogs]</c>.</param>
/// <param name="Address">Address of its <c>catalog.json</c>.</param>
/// <param name="Categories">Its categories.</param>
/// <param name="Apps">Its apps, in app ID order.</param>
/// <param name="Warnings">Entries that were skipped, one <c>/var/log/syslog</c> line each.</param>
public sealed record AppCatalogListing(
    string Name,
    Uri Address,
    IReadOnlyList<AppCatalogCategory> Categories,
    IReadOnlyList<AppCatalogEntry> Apps,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Gets one app (not a theme pack), or <see langword="null"/>.</summary>
    public AppCatalogEntry? Find(string appId) => Find(appId, AppCatalogEntryKind.App);

    /// <summary>Gets one entry of a kind, or <see langword="null"/>.</summary>
    /// <param name="id">App or pack id.</param>
    /// <param name="kind">What it must be.</param>
    public AppCatalogEntry? Find(string id, AppCatalogEntryKind kind) =>
        Apps.FirstOrDefault(app => app.Kind == kind && string.Equals(app.AppId, id, StringComparison.Ordinal));

    /// <summary>Gets the address of a file of the catalog, next to <c>catalog.json</c>.</summary>
    /// <param name="relativePath">A path of <c>catalog.json</c>, such as <c>packages/org.x.y/1.0.0/org.x.y-1.0.0.hackapp</c>.</param>
    /// <exception cref="ArgumentException">The path leaves the catalog.</exception>
    public Uri Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (!AppPackage.IsSafeRelativePath(relativePath))
        {
            throw new ArgumentException($"'{relativePath}' is not a relative path inside the catalog.", nameof(relativePath));
        }

        string escaped = string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));
        return new Uri(new Uri(Address, "./"), escaped);
    }
}

/// <summary>
/// Reads <c>catalog.json</c>, the file the publish workflow of a catalog repository writes on its
/// <c>packages</c> branch (ADR 0188 §6; format in <c>HackerOs-AppMarketPlace/doc/en/catalog-format.md</c>).
/// </summary>
/// <remarks>
/// A file of a newer schema, or that is not JSON, is refused whole. One invalid app or version is
/// skipped with a warning, so a single broken entry does not hide the rest of the catalog.
/// <c>kind</c> is <c>app</c> (the default) or <c>theme-pack</c> (ADR 0213 §2), whose versions are
/// <c>.hacktheme</c> files of at most 30 MiB; any other kind is skipped.
/// </remarks>
public static class AppCatalogDocument
{
    /// <summary>Newest <c>schemaVersion</c> this OS reads.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>The <c>kind</c> of an app.</summary>
    public const string AppKind = "app";

    /// <summary>The <c>kind</c> of a theme pack (ADR 0213 §2).</summary>
    public const string ThemePackKind = "theme-pack";

    /// <summary>Largest <c>catalog.json</c> accepted, in bytes.</summary>
    public const int MaximumBytes = 8 * 1024 * 1024;

    /// <summary>Reads a catalog.</summary>
    /// <param name="name">Catalog name in <c>[catalogs]</c>.</param>
    /// <param name="address">Address it was downloaded from.</param>
    /// <param name="bytes">The <c>catalog.json</c> bytes.</param>
    /// <exception cref="AppPackageException"><c>apps.catalog.too-large</c>, <c>apps.catalog.unsupported</c> or <c>apps.catalog.invalid</c>.</exception>
    public static AppCatalogListing Parse(string name, Uri address, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaximumBytes)
        {
            throw new AppPackageException("apps.catalog.too-large", $"The catalog '{name}' is larger than 8 MiB.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            int schema = root.GetProperty("schemaVersion").GetInt32();
            if (schema > SupportedSchemaVersion || schema < 1)
            {
                throw new AppPackageException(
                    "apps.catalog.unsupported", $"The catalog '{name}' has schemaVersion {schema}; this OS reads {SupportedSchemaVersion}.");
            }

            List<string> warnings = [];
            List<AppCatalogCategory> categories = [];
            if (root.TryGetProperty("categories", out JsonElement categoryArray) && categoryArray.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in categoryArray.EnumerateArray())
                {
                    string id = ReadString(item, "id");
                    if (id.Length == 0 || categories.Any(category => category.Id == id))
                    {
                        warnings.Add($"apps: catalog {name}: category '{id}' skipped");
                        continue;
                    }

                    categories.Add(new AppCatalogCategory(id, ReadMap(item, "labels")));
                }
            }

            List<AppCatalogEntry> apps = [];
            foreach (JsonElement item in root.GetProperty("apps").EnumerateArray())
            {
                if (TryReadApp(name, item, warnings) is { } app)
                {
                    if (apps.Any(existing => existing.AppId == app.AppId))
                    {
                        warnings.Add($"apps: catalog {name}: {app.AppId} listed twice, second entry skipped");
                        continue;
                    }

                    apps.Add(app);
                }
            }

            return new AppCatalogListing(
                name, address, categories, [.. apps.OrderBy(static app => app.AppId, StringComparer.Ordinal)], warnings);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new AppPackageException("apps.catalog.invalid", $"The catalog '{name}' is not valid: {exception.Message}");
        }
    }

    private static AppCatalogEntry? TryReadApp(string catalog, JsonElement item, List<string> warnings)
    {
        string appId = item.ValueKind == JsonValueKind.Object ? ReadString(item, "appId") : string.Empty;
        string where = $"apps: catalog {catalog}: {(appId.Length == 0 ? "an app" : appId)}";
        if (!AppPackageSource.IsValidAppId(appId))
        {
            warnings.Add($"{where}: invalid app ID, skipped");
            return null;
        }

        string kindText = item.TryGetProperty("kind", out JsonElement kindElement) ? kindElement.GetString() ?? AppKind : AppKind;
        AppCatalogEntryKind kind;
        switch (kindText)
        {
            case AppKind:
                kind = AppCatalogEntryKind.App;
                break;
            case ThemePackKind when ThemePacks.ThemePack.IsValidId(appId):
                kind = AppCatalogEntryKind.ThemePack;
                break;
            case ThemePackKind:
                warnings.Add($"{where}: invalid theme pack id, skipped");
                return null;
            default:
                warnings.Add($"{where}: kind '{kindText}' is not supported, skipped");
                return null;
        }

        List<AppCatalogVersion> versions = [];
        if (item.TryGetProperty("versions", out JsonElement versionArray) && versionArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement version in versionArray.EnumerateArray())
            {
                if (TryReadVersion(version, kind) is { } parsed && versions.All(existing => existing.Version != parsed.Version))
                {
                    versions.Add(parsed);
                }
                else
                {
                    warnings.Add($"{where}: version '{ReadString(version, "version")}' skipped");
                }
            }
        }

        string name = ReadString(item, "name");
        if (versions.Count == 0 || name.Length == 0)
        {
            warnings.Add($"{where}: no valid version or no name, skipped");
            return null;
        }

        versions.Sort(static (left, right) => right.Version.CompareTo(left.Version));
        SemanticVersion latest = SemanticVersion.TryParse(ReadString(item, "latestVersion"), out SemanticVersion announced)
            && versions.Any(version => version.Version == announced)
                ? announced
                : versions.FirstOrDefault(static version => version.Version.PreRelease is null)?.Version ?? versions[0].Version;
        return new AppCatalogEntry(
            appId,
            name,
            ReadString(item, "category"),
            ReadStrings(item, "tags"),
            ReadMap(item, "shortDescriptions"),
            ReadString(item, "license"),
            ReadString(item, "repository"),
            latest,
            versions)
        {
            Kind = kind,
        };
    }

    private static AppCatalogVersion? TryReadVersion(JsonElement item, AppCatalogEntryKind kind)
    {
        (string extension, long maximumSize) = kind == AppCatalogEntryKind.ThemePack
            ? (Theming.Abstractions.ThemePackLayout.Extension, ThemePacks.ThemePack.MaximumBytes)
            : (AppPackage.Extension, AppPackage.MaximumArchiveBytes);
        if (item.ValueKind != JsonValueKind.Object
            || !SemanticVersion.TryParse(ReadString(item, "version"), out SemanticVersion version))
        {
            return null;
        }

        string package = ReadString(item, "package");
        string sha256 = ReadString(item, "sha256").ToLowerInvariant();
        long size = item.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long bytes) ? bytes : -1;
        if (!AppPackage.IsSafeRelativePath(package)
            || !package.EndsWith(extension, StringComparison.Ordinal)
            || sha256.Length != 64
            || !sha256.All(Uri.IsHexDigit)
            || size < 0
            || size > maximumSize)
        {
            return null;
        }

        string? minimum = null;
        string? maximum = null;
        if (item.TryGetProperty("sdkCompatibility", out JsonElement sdk) && sdk.ValueKind == JsonValueKind.Object)
        {
            minimum = NullIfEmpty(ReadString(sdk, "minimumVersion"));
            maximum = NullIfEmpty(ReadString(sdk, "maximumVersion"));
        }

        return new AppCatalogVersion(version, ReadString(item, "commit"), package, sha256, size, ReadStrings(item, "capabilities"))
        {
            PublishedAt = DateTimeOffset.TryParse(ReadString(item, "publishedAt"), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset at)
                ? at
                : null,
            MinimumSdkVersion = minimum,
            MaximumSdkVersion = maximum,
            Icon = SafePathOrNull(ReadString(item, "icon")),
            Screenshots = [.. ReadStrings(item, "screenshots").Where(AppPackage.IsSafeRelativePath)],
            Descriptions = ReadMap(item, "descriptions")
                .Where(static pair => AppPackage.IsSafeRelativePath(pair.Value))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase),
        };
    }

    private static string? SafePathOrNull(string path) => path.Length > 0 && AppPackage.IsSafeRelativePath(path) ? path : null;

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static string ReadString(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static IReadOnlyList<string> ReadStrings(JsonElement item, string property) =>
        item.TryGetProperty(property, out JsonElement array) && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray().Where(static value => value.ValueKind == JsonValueKind.String).Select(static value => value.GetString()!)]
            : [];

    private static IReadOnlyDictionary<string, string> ReadMap(JsonElement item, string property)
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        if (item.TryGetProperty(property, out JsonElement element) && element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty entry in element.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is { Length: > 0 } text)
                {
                    map[entry.Name] = text;
                }
            }
        }

        return map;
    }
}
