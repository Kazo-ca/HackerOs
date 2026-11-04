using System.Text.Json.Serialization;
using HackerOs.AppSdk.IO;

namespace HackerOs.AppSdk.Net.Hosting;

/// <summary>
/// Where a hosted static site is declared (ADR 0077, section 1), which decides who may write it and
/// which ports it may bind.
/// </summary>
public enum HackWebSiteScope
{
    /// <summary>
    /// <c>/etc/hackweb/sites/</c>: written by an administrator, any port, served as the
    /// <c>www-data</c> account (like Apache configured by root).
    /// </summary>
    System,

    /// <summary>
    /// <c>~/.config/hackweb/sites/</c>: written by any user for themselves, ports 1024 and up only,
    /// served with that user's rights (like <c>python -m http.server</c>).
    /// </summary>
    User
}

/// <summary>
/// One hosted static site: the content of a <c>.site</c> file (JSON), the Kazo OS counterpart of a
/// site in IIS's <c>applicationHost.config</c> or of an nginx file in <c>sites-available</c>
/// (ADR 0077, section 1). <c>hackwebd</c> serves it; Web Server Manager edits it.
/// </summary>
/// <example>
/// <code>
/// {
///   "name": "docs",
///   "root": "/home/benoit/www/docs",
///   "bindings": ["http://localhost:8080/", "http://docs.hack:8080/"],
///   "defaultDocuments": ["index.html", "index.htm"],
///   "directoryBrowsing": false,
///   "enabled": true
/// }
/// </code>
/// </example>
public sealed record HackWebSite
{
    /// <summary>Default documents of a new site, in IIS's order for static content.</summary>
    public static readonly IReadOnlyList<string> DefaultDefaultDocuments = ["index.html", "index.htm", "default.htm"];

    /// <summary>Gets the site name: also the file name (<c>&lt;name&gt;.site</c>) and its log names.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the absolute directory whose files are served (the document root).</summary>
    [JsonPropertyName("root")]
    public string Root { get; init; } = string.Empty;

    /// <summary>
    /// Gets the listener prefixes, <c>http://host[:port]/path/</c>; the first one is the site's main
    /// address. A host other than <c>localhost</c> is published by <c>hackdns</c> (ADR 0074, section 4).
    /// </summary>
    [JsonPropertyName("bindings")]
    public IReadOnlyList<string> Bindings { get; init; } = [];

    /// <summary>Gets the files tried, in order, when a request names a directory.</summary>
    [JsonPropertyName("defaultDocuments")]
    public IReadOnlyList<string> DefaultDocuments { get; init; } = DefaultDefaultDocuments;

    /// <summary>Gets whether a directory with no default document is listed ("Index of"); otherwise <c>403</c>.</summary>
    [JsonPropertyName("directoryBrowsing")]
    public bool DirectoryBrowsing { get; init; }

    /// <summary>Gets whether <c>hackwebd</c> serves the site; a stopped site keeps its file.</summary>
    /// <remarks>
    /// A settable property on purpose: the JSON source generator builds init-only properties through
    /// an object initializer and gives an absent one <c>default</c>, so an <c>init</c> here would turn a
    /// file with no <c>"enabled"</c> into a stopped site. A setter is only called when the key exists.
    /// </remarks>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

/// <summary>A <c>.site</c> file read from disk, with where it lives and what is wrong with it.</summary>
/// <param name="Path">Absolute path of the <c>.site</c> file.</param>
/// <param name="Scope">Where it is declared.</param>
/// <param name="Owner">Login of the user who declared it (<see cref="HackWebSiteScope.User"/>), or <see langword="null"/>.</param>
/// <param name="Site">The parsed site, or <see langword="null"/> when the JSON could not be read.</param>
/// <param name="Errors">Validation errors; empty when the site can be served.</param>
public sealed record HackWebSiteDeclaration(
    string Path,
    HackWebSiteScope Scope,
    string? Owner,
    HackWebSite? Site,
    IReadOnlyList<string> Errors)
{
    /// <summary>Gets whether the declaration is valid (it may still be disabled).</summary>
    public bool IsValid => Site is not null && Errors.Count == 0;

    /// <summary>Gets whether <c>hackwebd</c> should serve it: valid and enabled.</summary>
    public bool IsServable => IsValid && Site!.Enabled;

    /// <summary>Gets the site name, or the file name without <c>.site</c> when the JSON is unreadable.</summary>
    public string Name => Site?.Name is { Length: > 0 } name
        ? name
        : HackPath.GetFileNameWithoutExtension(Path) ?? Path;
}
