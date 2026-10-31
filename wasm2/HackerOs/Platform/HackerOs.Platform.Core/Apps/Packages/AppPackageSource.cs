using System.Diagnostics.CodeAnalysis;
using HackerOs.App.Abstractions;

namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>
/// Where an app package comes from: a GitHub repository that publishes HackerOS app packages
/// (ADR 0182 §2, §4), written <c>github:Kazo-ca/HackerOs-App-Webamp</c>, or an app of a catalog of
/// <c>[catalogs]</c> (ADR 0188, ADR 0212 §1), written <c>catalog:official/org.example.notes</c>;
/// either optionally followed by <c>@1.0.0</c>.
/// </summary>
/// <param name="Owner">GitHub owner (user or organization), compared without case like GitHub does; for a catalog app, the catalog name.</param>
/// <param name="Repository">Repository name; for a catalog app, the app ID.</param>
/// <param name="Version">Requested package version, or <see langword="null"/> for the newest one in <c>index.json</c>.</param>
public sealed record AppPackageSource(string Owner, string Repository, SemanticVersion? Version = null)
{
    /// <summary>Prefix of the canonical form written in <c>apps.config</c>.</summary>
    public const string GitHubScheme = "github:";

    /// <summary>Branch of the app repository that holds <c>index.json</c> and the packages (ADR 0182 §2).</summary>
    public const string PackagesBranch = "hackeros-packages";

    /// <summary>Name of the version list at the root of <see cref="PackagesBranch"/>.</summary>
    public const string IndexFileName = "index.json";

    /// <summary>Prefix of an app of a catalog (ADR 0212 §1), e.g. <c>catalog:official/org.example.notes</c>.</summary>
    public const string CatalogScheme = "catalog:";

    /// <summary>Gets whether this is an app of a catalog rather than a GitHub repository.</summary>
    public bool IsCatalog { get; init; }

    /// <summary>Gets the catalog name of a catalog app, or <see langword="null"/>.</summary>
    public string? Catalog => IsCatalog ? Owner : null;

    /// <summary>Gets the app of a catalog source, or <see langword="null"/> for a repository.</summary>
    public string? CatalogAppId => IsCatalog ? Repository : null;

    /// <summary>Creates the source of an app of a catalog.</summary>
    /// <param name="catalog">Catalog name of <c>[catalogs]</c>.</param>
    /// <param name="appId">The app.</param>
    /// <param name="version">A version, or <see langword="null"/> for the newest stable one.</param>
    public static AppPackageSource ForCatalog(string catalog, string appId, SemanticVersion? version = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return new AppPackageSource(catalog.ToLowerInvariant(), appId, version) { IsCatalog = true };
    }

    /// <summary>
    /// Gets <c>github.com/&lt;owner&gt;</c>, the key of <c>[trusted]</c> in <c>app-sources.config</c>;
    /// <c>catalog:&lt;name&gt;</c> for a catalog app.
    /// </summary>
    public string TrustKey => IsCatalog ? $"{CatalogScheme}{Owner}" : $"github.com/{Owner}";

    /// <summary>
    /// Gets <c>github.com/&lt;owner&gt;/&lt;repository&gt;</c>, the key that trusts this repository alone (ADR 0189 §1);
    /// a catalog is trusted as a whole, so a catalog app gives <see cref="TrustKey"/>.
    /// </summary>
    public string RepositoryTrustKey => IsCatalog ? TrustKey : $"github.com/{Owner}/{Repository}";

    /// <summary>
    /// Gets the canonical form without version, e.g. <c>github:Kazo-ca/HackerOs-App-Webamp</c> or
    /// <c>catalog:official/org.example.notes</c>.
    /// </summary>
    public string Name => $"{(IsCatalog ? CatalogScheme : GitHubScheme)}{Owner}/{Repository}";

    /// <summary>Gets the canonical form, with <c>@version</c> when one is set.</summary>
    public override string ToString() => Version is null ? Name : $"{Name}@{Version}";

    /// <summary>Gets the same source pinned to <paramref name="version"/>.</summary>
    public AppPackageSource WithVersion(SemanticVersion version) => this with { Version = version };

    /// <summary>Gets the URL of <c>index.json</c> on <paramref name="host"/> (e.g. <c>raw.githubusercontent.com</c>).</summary>
    public Uri GetIndexUri(string host) => GetFileUri(host, IndexFileName);

    /// <summary>Gets the URL of a file of <see cref="PackagesBranch"/>, relative to its root.</summary>
    /// <param name="host">Download host listed in <c>[hosts]</c>.</param>
    /// <param name="relativePath">Path from <c>index.json</c>, e.g. <c>1.0.0/org.kazo.webamp-1.0.0.hackapp</c>.</param>
    /// <exception cref="InvalidOperationException">This is a catalog app: its files are next to the catalog's <c>catalog.json</c>.</exception>
    public Uri GetFileUri(string host, string relativePath)
    {
        if (IsCatalog)
        {
            throw new InvalidOperationException($"{Name} is an app of a catalog, not a repository.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (relativePath.StartsWith('/') || relativePath.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException($"'{relativePath}' is not a relative path inside the packages branch.", nameof(relativePath));
        }

        string escaped = string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));
        return new Uri($"https://{host}/{Owner}/{Repository}/{PackagesBranch}/{escaped}", UriKind.Absolute);
    }

    /// <summary>
    /// Reads <c>github:owner/repo</c>, <c>owner/repo</c>, <c>https://github.com/owner/repo</c> or
    /// <c>catalog:name/app.id</c>, each optionally followed by <c>@version</c>.
    /// </summary>
    /// <param name="text">What the user typed.</param>
    /// <param name="source">The source, when the text is valid.</param>
    public static bool TryParse(string? text, [NotNullWhen(true)] out AppPackageSource? source)
    {
        source = null;
        string value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return false;
        }

        SemanticVersion? version = null;
        int at = value.LastIndexOf('@');
        if (at >= 0)
        {
            if (!SemanticVersion.TryParse(value[(at + 1)..], out SemanticVersion parsed))
            {
                return false;
            }

            version = parsed;
            value = value[..at];
        }

        if (value.StartsWith(CatalogScheme, StringComparison.OrdinalIgnoreCase))
        {
            string[] catalogParts = value[CatalogScheme.Length..].Split('/');
            if (catalogParts.Length != 2
                || !AppSourcesConfigDocument.IsCatalogName(catalogParts[0].ToLowerInvariant())
                || !IsValidAppId(catalogParts[1]))
            {
                return false;
            }

            source = ForCatalog(catalogParts[0], catalogParts[1], version);
            return true;
        }

        if (value.StartsWith(GitHubScheme, StringComparison.OrdinalIgnoreCase))
        {
            value = value[GitHubScheme.Length..];
        }
        else if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
                || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment))
            {
                return false;
            }

            value = uri.AbsolutePath.Trim('/');
            if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^4];
            }
        }

        string[] parts = value.Split('/');
        if (parts.Length != 2 || !IsValidName(parts[0]) || !IsValidName(parts[1]))
        {
            return false;
        }

        source = new AppPackageSource(parts[0], parts[1], version);
        return true;
    }

    /// <summary>Equality ignores the case of the owner and the repository, like GitHub.</summary>
    public bool Equals(AppPackageSource? other) =>
        other is not null
        && IsCatalog == other.IsCatalog
        && string.Equals(Owner, other.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Repository, other.Repository, StringComparison.OrdinalIgnoreCase)
        && Nullable.Equals(Version, other.Version);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        IsCatalog,
        StringComparer.OrdinalIgnoreCase.GetHashCode(Owner),
        StringComparer.OrdinalIgnoreCase.GetHashCode(Repository),
        Version);

    /// <summary>Gets whether two sources name the same repository, whatever their versions.</summary>
    public bool IsSameRepository(AppPackageSource other) =>
        IsCatalog == other.IsCatalog
        && string.Equals(Owner, other.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Repository, other.Repository, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An app ID as <c>AppManifestValidator</c> accepts it: at least three dot-separated segments of
    /// lower-case letters, digits and dashes.
    /// </summary>
    internal static bool IsValidAppId(string appId) =>
        appId.Length is > 0 and <= 128
        && appId.Split('.') is { Length: >= 3 } segments
        && segments.All(static segment => segment.Length > 0
            && segment.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'));

    internal static bool IsValidName(string name) =>
        name.Length is > 0 and <= 100
        && name is not "." and not ".."
        && name.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
