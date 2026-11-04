using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.IO;

namespace Kazo.Os.AppSdk.Net.Hosting;

/// <summary>
/// The <c>.site</c> file format of hosted static sites (ADR 0077, section 1): where the files live,
/// how they are read, written and validated. Shared by <c>hackwebd</c>, Web Server Manager and the
/// OS's resolver (which publishes the bound names), so they all agree on what a valid site is.
/// </summary>
public static class KazoWebSiteFile
{
    /// <summary>Directory of the sites an administrator declares.</summary>
    public const string SystemSitesDirectory = "/etc/hackweb/sites";

    /// <summary>Extension of a site file.</summary>
    public const string Extension = ".site";

    /// <summary>Longest site name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Gets the directory of the sites a user declares: <c>~/.config/hackweb/sites</c>.</summary>
    /// <param name="homeDirectory">The user's home, such as <c>/home/benoit</c>.</param>
    public static string GetUserSitesDirectory(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        return $"{homeDirectory.TrimEnd('/')}/.config/hackweb/sites";
    }

    /// <summary>Gets the path of the file of site <paramref name="name"/> in <paramref name="directory"/>.</summary>
    /// <param name="directory">A sites directory.</param>
    /// <param name="name">A valid site name.</param>
    public static string GetPath(string directory, string name) => $"{directory.TrimEnd('/')}/{name}{Extension}";

    /// <summary>Serializes a site as the indented JSON of a <c>.site</c> file, ending with a line break.</summary>
    /// <param name="site">The site.</param>
    public static string Serialize(KazoWebSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return JsonSerializer.Serialize(site, KazoWebSiteJsonContext.Default.KazoWebSite) + "\n";
    }

    /// <summary>
    /// Parses and validates the content of a <c>.site</c> file, like <c>nginx -t</c>: a file that
    /// cannot be read gives a declaration with no site and one error.
    /// </summary>
    /// <param name="path">Absolute path of the file (its name must be <c>&lt;name&gt;.site</c>).</param>
    /// <param name="content">The JSON.</param>
    /// <param name="scope">Where the file lives.</param>
    /// <param name="owner">The declaring user, for a user site.</param>
    public static KazoWebSiteDeclaration Parse(string path, string content, KazoWebSiteScope scope, string? owner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        KazoWebSite? site;
        try
        {
            site = JsonSerializer.Deserialize(content, KazoWebSiteJsonContext.Default.KazoWebSite);
        }
        catch (JsonException exception)
        {
            string where = exception.LineNumber is { } line ? $" (line {line + 1})" : string.Empty;
            return new KazoWebSiteDeclaration(path, scope, owner, null, [$"invalid JSON{where}: {exception.Message}"]);
        }

        if (site is null)
        {
            return new KazoWebSiteDeclaration(path, scope, owner, null, ["the file holds no site object"]);
        }

        // Absent keys come back null from the source generator (init-only properties), not with their defaults.
        site = site with
        {
            Name = site.Name ?? string.Empty,
            Root = site.Root ?? string.Empty,
            Bindings = site.Bindings ?? [],
            DefaultDocuments = site.DefaultDocuments ?? KazoWebSite.DefaultDefaultDocuments
        };
        List<string> errors = [.. Validate(site, scope)];
        string? fileName = KazoPath.GetFileName(path);
        if (errors.Count == 0 && !string.Equals(fileName, site.Name + Extension, StringComparison.Ordinal))
        {
            errors.Add($"the file of site '{site.Name}' must be named '{site.Name}{Extension}'");
        }

        return new KazoWebSiteDeclaration(path, scope, owner, site, errors);
    }

    /// <summary>Validates a site for <paramref name="scope"/>; an empty list means it can be served.</summary>
    /// <param name="site">The site.</param>
    /// <param name="scope">Where it is (or will be) declared.</param>
    /// <returns>One human-readable message per problem, in English.</returns>
    public static IReadOnlyList<string> Validate(KazoWebSite site, KazoWebSiteScope scope) => Validate(site, scope, context: null);

    /// <summary>
    /// Validates a site for <paramref name="scope"/>, with the messages in the language of
    /// <paramref name="context"/>'s process (ADR 0104), for an app that shows them as form errors.
    /// </summary>
    /// <param name="site">The site.</param>
    /// <param name="scope">Where it is (or will be) declared.</param>
    /// <param name="context">The app showing the messages; English when <see langword="null"/>.</param>
    /// <returns>One human-readable message per problem; an empty list means it can be served.</returns>
    public static IReadOnlyList<string> Validate(KazoWebSite site, KazoWebSiteScope scope, IAppExecutionContext? context)
    {
        ArgumentNullException.ThrowIfNull(site);
        Microsoft.Extensions.Localization.IStringLocalizer<Strings> text = KazoLocalization.GetLocalizer<Strings>(context);
        bool english = context is null
            || KazoLocale.ResolveLanguage(context.Environment.GetVariable).TwoLetterISOLanguageName == KazoLocale.DefaultLanguage.TwoLetterISOLanguageName;
        List<string> errors = [];
        if (!IsValidName(site.Name))
        {
            errors.Add(text["Site.InvalidName", site.Name, MaxNameLength]);
        }

        if (string.IsNullOrWhiteSpace(site.Root) || !site.Root.StartsWith('/'))
        {
            errors.Add(text["Site.RootNotAbsolute"]);
        }
        else if (site.Root.Trim('/').Length == 0 || site.Root.Split('/').Any(static segment => segment is "." or ".."))
        {
            errors.Add(text["Site.RootRefused", site.Root]);
        }

        if (site.Bindings.Count == 0)
        {
            errors.Add(text["Site.NoBinding"]);
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string binding in site.Bindings)
        {
            if (!WebAppUrls.TryParse(binding, allowWildcardHost: true, out WebBinding parsed, out string? error))
            {
                // WebAppUrls (App Abstractions) words its reasons in English only: another language
                // names the binding in its own words and quotes that reason.
                errors.Add(english ? error! : text["Site.InvalidBinding", binding, error ?? string.Empty]);
                continue;
            }

            if (parsed.Scheme != "http")
            {
                errors.Add(text["Site.HttpsBinding", binding]);
                continue;
            }

            if (!seen.Add(parsed.ToString()))
            {
                errors.Add(text["Site.DuplicateBinding", binding]);
            }

            if (scope == KazoWebSiteScope.User && parsed.IsPrivilegedPort)
            {
                errors.Add(text["Site.PrivilegedPort", parsed.Port, binding, WebAppUrls.FirstUnprivilegedPort, SystemSitesDirectory]);
            }
        }

        foreach (string document in site.DefaultDocuments)
        {
            if (string.IsNullOrWhiteSpace(document) || document.Contains('/', StringComparison.Ordinal) || document is "." or "..")
            {
                errors.Add(text["Site.BadDefaultDocument", document]);
            }
        }

        return errors;
    }

    /// <summary>
    /// Parses one binding: an <c>http://</c> listener prefix, host name or <c>+</c>/<c>*</c>, optional
    /// port, path ending with <c>/</c>. <c>https</c> is refused: a binding would need a certificate.
    /// </summary>
    /// <param name="value">The binding.</param>
    /// <param name="binding">The parsed binding.</param>
    /// <param name="error">Why it was refused.</param>
    public static bool TryParseBinding(string? value, out WebBinding binding, out string? error)
    {
        if (!WebAppUrls.TryParse(value, allowWildcardHost: true, out binding, out error))
        {
            return false;
        }

        if (binding.Scheme != "http")
        {
            error = KazoLocalization.GetLocalizer<Strings>(null)["Site.HttpsBinding", value ?? string.Empty];
            return false;
        }

        return true;
    }

    /// <summary>Gets whether <paramref name="name"/> is a valid site name.</summary>
    /// <param name="name">The candidate name.</param>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= MaxNameLength
        && char.IsAsciiLetterOrDigit(name[0])
        && name.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    /// <summary>
    /// Reads every <c>.site</c> file of the system directory and of the calling user's directory, with
    /// the caller's rights. A missing directory gives nothing; a file that cannot be read gives a
    /// declaration with an error.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>System sites first, then the user's, each sorted by file name.</returns>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static async Task<IReadOnlyList<KazoWebSiteDeclaration>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        KazoUserInfo user = KazoUser.Current;
        List<KazoWebSiteDeclaration> declarations = [];
        declarations.AddRange(await ReadDirectoryAsync(SystemSitesDirectory, KazoWebSiteScope.System, null, cancellationToken).ConfigureAwait(false));
        declarations.AddRange(await ReadDirectoryAsync(GetUserSitesDirectory(user.HomeDirectory), KazoWebSiteScope.User, user.LoginName, cancellationToken).ConfigureAwait(false));
        return declarations;
    }

    /// <summary>Reads the <c>.site</c> files of one directory with the caller's rights.</summary>
    /// <param name="directory">The directory.</param>
    /// <param name="scope">Its scope.</param>
    /// <param name="owner">The declaring user of a user directory.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<IReadOnlyList<KazoWebSiteDeclaration>> ReadDirectoryAsync(
        string directory, KazoWebSiteScope scope, string? owner, CancellationToken cancellationToken = default)
    {
        if (!await KazoDirectory.ExistsAsync(directory, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        List<KazoWebSiteDeclaration> declarations = [];
        string[] files = await KazoDirectory.GetFilesAsync(directory, "*" + Extension, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (string file in files.Order(StringComparer.Ordinal))
        {
            try
            {
                string content = await KazoFile.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                declarations.Add(Parse(file, content, scope, owner));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                declarations.Add(new KazoWebSiteDeclaration(file, scope, owner, null, [$"cannot read the file: {exception.Message}"]));
            }
        }

        return declarations;
    }
}

/// <summary>
/// Trimming-safe JSON of a <c>.site</c> file: indented on write; comments, trailing commas and any
/// property-name case accepted on read, as an administrator editing it by hand would expect.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(KazoWebSite))]
internal sealed partial class KazoWebSiteJsonContext : JsonSerializerContext;
