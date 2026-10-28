using System.Diagnostics.CodeAnalysis;
using HackerOs.App.Abstractions;

namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>
/// A GitHub repository that publishes HackerOS app packages (ADR 0182 §2, §4): written
/// <c>github:Kazo-ca/HackerOs-App-Webamp</c>, optionally followed by <c>@1.0.0</c>.
/// </summary>
/// <param name="Owner">GitHub owner (user or organization), compared without case like GitHub does.</param>
/// <param name="Repository">Repository name.</param>
/// <param name="Version">Requested package version, or <see langword="null"/> for the newest one in <c>index.json</c>.</param>
public sealed record AppPackageSource(string Owner, string Repository, SemanticVersion? Version = null)
{
    /// <summary>Prefix of the canonical form written in <c>apps.config</c>.</summary>
    public const string GitHubScheme = "github:";

    /// <summary>Branch of the app repository that holds <c>index.json</c> and the packages (ADR 0182 §2).</summary>
    public const string PackagesBranch = "hackeros-packages";

    /// <summary>Name of the version list at the root of <see cref="PackagesBranch"/>.</summary>
    public const string IndexFileName = "index.json";

    /// <summary>Gets <c>github.com/&lt;owner&gt;</c>, the key of <c>[trusted]</c> in <c>app-sources.config</c>.</summary>
    public string TrustKey => $"github.com/{Owner}";

    /// <summary>Gets the canonical form without version, e.g. <c>github:Kazo-ca/HackerOs-App-Webamp</c>.</summary>
    public string Name => $"{GitHubScheme}{Owner}/{Repository}";

    /// <summary>Gets the canonical form, with <c>@version</c> when one is set.</summary>
    public override string ToString() => Version is null ? Name : $"{Name}@{Version}";

    /// <summary>Gets the same source pinned to <paramref name="version"/>.</summary>
    public AppPackageSource WithVersion(SemanticVersion version) => this with { Version = version };

    /// <summary>Gets the URL of <c>index.json</c> on <paramref name="host"/> (e.g. <c>raw.githubusercontent.com</c>).</summary>
    public Uri GetIndexUri(string host) => GetFileUri(host, IndexFileName);

    /// <summary>Gets the URL of a file of <see cref="PackagesBranch"/>, relative to its root.</summary>
    /// <param name="host">Download host listed in <c>[hosts]</c>.</param>
    /// <param name="relativePath">Path from <c>index.json</c>, e.g. <c>1.0.0/org.kazo.webamp-1.0.0.hackapp</c>.</param>
    public Uri GetFileUri(string host, string relativePath)
    {
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
    /// Reads <c>github:owner/repo</c>, <c>owner/repo</c> or <c>https://github.com/owner/repo</c>,
    /// each optionally followed by <c>@version</c>.
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
        && string.Equals(Owner, other.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Repository, other.Repository, StringComparison.OrdinalIgnoreCase)
        && Nullable.Equals(Version, other.Version);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(Owner),
        StringComparer.OrdinalIgnoreCase.GetHashCode(Repository),
        Version);

    /// <summary>Gets whether two sources name the same repository, whatever their versions.</summary>
    public bool IsSameRepository(AppPackageSource other) =>
        string.Equals(Owner, other.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Repository, other.Repository, StringComparison.OrdinalIgnoreCase);

    private static bool IsValidName(string name) =>
        name.Length is > 0 and <= 100
        && name is not "." and not ".."
        && name.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
