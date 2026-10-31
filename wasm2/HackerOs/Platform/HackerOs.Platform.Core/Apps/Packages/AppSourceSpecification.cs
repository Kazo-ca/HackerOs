using System.Diagnostics.CodeAnalysis;

namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>
/// A source as an administrator names it to <c>hackpkg sources add | remove</c> or in Settings ▸
/// Sources (ADR 0189 §1): a catalog, a GitHub owner, or one GitHub repository.
/// </summary>
/// <remarks>
/// Accepted forms:
/// <list type="bullet">
/// <item><c>&lt;name&gt;=https://…/catalog.json</c> or <c>https://…/catalog.json</c>: a catalog (its name is derived from the address when omitted);</item>
/// <item><c>catalog:&lt;name&gt;</c>: a catalog already listed, by name;</item>
/// <item><c>github:&lt;owner&gt;</c> or <c>github.com/&lt;owner&gt;</c>: every repository of an owner;</item>
/// <item><c>github:&lt;owner&gt;/&lt;repo&gt;</c>, <c>github.com/&lt;owner&gt;/&lt;repo&gt;</c>, <c>&lt;owner&gt;/&lt;repo&gt;</c> or <c>https://github.com/&lt;owner&gt;/&lt;repo&gt;</c>: one repository.</item>
/// </list>
/// </remarks>
/// <param name="Name">Catalog name, or the trust key of a GitHub owner or repository.</param>
/// <param name="TrustKey">Key of <c>[trusted]</c>: <c>catalog:&lt;name&gt;</c> or <c>github.com/&lt;owner&gt;[/&lt;repo&gt;]</c>.</param>
/// <param name="CatalogAddress">Address of the catalog's <c>catalog.json</c>, when given.</param>
public sealed record AppSourceSpecification(string Name, string TrustKey, Uri? CatalogAddress)
{
    private const int MaximumCatalogNameLength = 64;

    /// <summary>Gets whether the source is a catalog.</summary>
    public bool IsCatalog => TrustKey.StartsWith(AppSourcesConfigDocument.CatalogTrustPrefix, StringComparison.Ordinal);

    /// <summary>Reads what the administrator typed.</summary>
    /// <param name="text">The source.</param>
    /// <param name="source">The parsed source.</param>
    public static bool TryParse(string? text, [NotNullWhen(true)] out AppSourceSpecification? source)
    {
        source = null;
        string value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return false;
        }

        int equals = value.IndexOf('=', StringComparison.Ordinal);
        if (equals > 0)
        {
            string name = value[..equals].Trim();
            if (!AppSourcesConfigDocument.IsCatalogName(name)
                || !AppSourcesConfigDocument.TryParseCatalogAddress(value[(equals + 1)..].Trim(), out Uri? named))
            {
                return false;
            }

            source = Catalog(name, named);
            return true;
        }

        if (value.StartsWith(AppSourcesConfigDocument.CatalogTrustPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string name = value[AppSourcesConfigDocument.CatalogTrustPrefix.Length..];
            if (!AppSourcesConfigDocument.IsCatalogName(name))
            {
                return false;
            }

            source = Catalog(name, null);
            return true;
        }

        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            if (!AppSourcesConfigDocument.TryParseCatalogAddress(value, out Uri? address))
            {
                return false;
            }

            source = Catalog(DeriveCatalogName(address), address);
            return true;
        }

        return TryParseGitHub(value, out source);
    }

    private static bool TryParseGitHub(string value, [NotNullWhen(true)] out AppSourceSpecification? source)
    {
        source = null;
        string path = value;
        bool prefixed = false;
        foreach (string prefix in (string[])[AppPackageSource.GitHubScheme, "https://github.com/", "github.com/"])
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[prefix.Length..];
                prefixed = true;
                break;
            }
        }

        path = path.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        // A bare word is ambiguous; an owner alone needs its prefix (github:Kazo-ca).
        string[] parts = path.Split('/');
        if (!((prefixed && parts.Length == 1) || parts.Length == 2) || !parts.All(AppPackageSource.IsValidName))
        {
            return false;
        }

        string key = $"github.com/{string.Join('/', parts)}";
        source = new AppSourceSpecification(key, key, null);
        return true;
    }

    private static AppSourceSpecification Catalog(string name, Uri? address) =>
        new(name, AppSourcesConfigDocument.CatalogTrustPrefix + name, address);

    /// <summary>
    /// Names a catalog after its address: <c>&lt;owner&gt;-&lt;repo&gt;</c> for a GitHub raw address,
    /// the host name otherwise, in lower case with dashes.
    /// </summary>
    private static string DeriveCatalogName(Uri address)
    {
        string[] segments = address.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string raw = string.Equals(address.Host, AppSourcesConfigDocument.DefaultGitHubHost, StringComparison.OrdinalIgnoreCase)
            && segments.Length >= 2
            ? $"{segments[0]}-{segments[1]}"
            : address.Host;
        string dashed = new([.. raw.ToLowerInvariant().Select(static c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
        string name = string.Join('-', dashed.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return name.Length > MaximumCatalogNameLength ? name[..MaximumCatalogNameLength].TrimEnd('-') : name;
    }
}
