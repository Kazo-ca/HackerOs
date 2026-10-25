using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.FileSystem;

/// <summary>
/// Seeds a deterministic file/folder fixture into the signed-in user's home directory.
/// Only ever invoked from the "Test Demo" control, which is itself gated to E2E test
/// runs (see <c>doc/wasm/dev/old/e2e-test-demo-mode.md</c>). Writes go through the trusted platform
/// <see cref="IFileSystemService"/> as a system operation, the same way
/// <see cref="FileSystemSeeder"/> provisions the default home layout at login.
/// </summary>
public sealed class TestDemoFixtureSeeder(IFileSystemService fileSystem)
{
    private readonly IFileSystemService _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    /// <summary>Relative fixture root created under the user's home directory.</summary>
    public const string FixtureRootName = "e2e-fixtures";

    /// <summary>Name of the plain fixture file used by download/zip content assertions.</summary>
    public const string AlphaFileName = "alpha.txt";

    /// <summary>Content written to <see cref="AlphaFileName"/>.</summary>
    public const string AlphaContent = "Alpha fixture file for HackerOS E2E tests.\n";

    /// <summary>Name of the fixture file the Text Editor E2E scenario opens and overwrites.</summary>
    public const string EditableFileName = "editable.txt";

    /// <summary>Content <see cref="EditableFileName"/> is seeded with, before the editor rewrites it.</summary>
    public const string EditableOriginalContent = "Original content before edit.\n";

    /// <summary>Name of the nested fixture directory used to assert zip folder structure.</summary>
    public const string NestedDirectoryName = "notes";

    /// <summary>Name of the fixture file inside <see cref="NestedDirectoryName"/>.</summary>
    public const string NestedFileName = "beta.md";

    /// <summary>Content written to the nested fixture file.</summary>
    public const string NestedContent = "# Beta\n\nNested fixture file.\n";

    /// <summary>
    /// Name of the static HTML site fixture the Browser E2E scenario opens as <c>file://</c>
    /// (ADR 0075 §4). A sibling of <see cref="FixtureRootName"/> in <c>~/Documents</c>, so the
    /// File Explorer scenarios that zip the fixture root keep their exact content.
    /// </summary>
    public const string SiteDirectoryName = "e2e-site";

    /// <summary>Home page of the site fixture: a style sheet, a script, a link and a cross-origin fetch.</summary>
    // Explicit LF escapes keep fixture constants and public API baselines identical on Windows and Linux.
    public const string SiteIndexContent =
        "<!DOCTYPE html>\n" +
        "<html>\n" +
        "<head><meta charset=\"utf-8\"><title>E2E local site</title><link rel=\"stylesheet\" href=\"style.css\"></head>\n" +
        "<body>\n" +
        "<h1 id=\"title\">Local site</h1>\n" +
        "<p id=\"script-status\">script not run</p>\n" +
        "<p id=\"storage-status\"></p>\n" +
        "<p id=\"fetch-status\">fetch pending</p>\n" +
        "<a id=\"about-link\" href=\"about.html\">About</a>\n" +
        "<script src=\"app.js\"></script>\n" +
        "</body>\n" +
        "</html>";

    /// <summary>Style sheet of the site fixture.</summary>
    public const string SiteStyleContent = "#title { color: rgb(255, 0, 0); }\n";

    /// <summary>Script of the site fixture: counts visits in <c>localStorage</c> and calls a CORS API.</summary>
    // Explicit LF escapes keep fixture constants and public API baselines identical on Windows and Linux.
    public const string SiteScriptContent =
        "document.getElementById(\"script-status\").textContent = \"script ran\";\n" +
        "console.log(\"e2e-site-loaded\");\n" +
        "const visits = Number(localStorage.getItem(\"visits\") || \"0\") + 1;\n" +
        "localStorage.setItem(\"visits\", String(visits));\n" +
        "document.getElementById(\"storage-status\").textContent = \"visits=\" + visits;\n" +
        "fetch(\"https://example.hackeros/api/ping\")\n" +
        "    .then((response) => response.json())\n" +
        "    .then((body) => { document.getElementById(\"fetch-status\").textContent = body.pong ? \"pong\" : \"no pong\"; })\n" +
        "    .catch((error) => { document.getElementById(\"fetch-status\").textContent = \"fetch failed: \" + error; });";

    /// <summary>Second page of the site fixture, reached by a link.</summary>
    public const string SiteAboutContent = "<!DOCTYPE html><html><head><title>About</title></head><body><h1 id=\"about\">About page</h1></body></html>\n";

    /// <summary>Creates (or resets) the fixture tree for the given signed-in principal.</summary>
    /// <returns>The absolute virtual path of the seeded fixture root directory.</returns>
    public async Task<string> SeedAsync(AuthenticatedPrincipal principal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        string userId = principal.LoginName.Value;
        FileSystemAuthorizationContext context = CreateContext(userId, principal.PrimaryGroupId.ToString());

        string root = $"/home/{userId}/Documents/{FixtureRootName}";
        string nested = $"{root}/{NestedDirectoryName}";

        await EnsureDirectoryAsync(root, context, cancellationToken);
        await EnsureDirectoryAsync(nested, context, cancellationToken);
        await WriteFileAsync($"{root}/{AlphaFileName}", AlphaContent, context, cancellationToken);
        await WriteFileAsync($"{root}/{EditableFileName}", EditableOriginalContent, context, cancellationToken);
        await WriteFileAsync($"{nested}/{NestedFileName}", NestedContent, context, cancellationToken);

        string site = $"/home/{userId}/Documents/{SiteDirectoryName}";
        await EnsureDirectoryAsync(site, context, cancellationToken);
        await WriteFileAsync($"{site}/index.html", SiteIndexContent, context, cancellationToken);
        await WriteFileAsync($"{site}/style.css", SiteStyleContent, context, cancellationToken);
        await WriteFileAsync($"{site}/app.js", SiteScriptContent, context, cancellationToken);
        await WriteFileAsync($"{site}/about.html", SiteAboutContent, context, cancellationToken);

        return root;
    }

    private async Task EnsureDirectoryAsync(
        string pathValue,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken)
    {
        VirtualPath path = VirtualPath.Parse(pathValue);
        FileSystemResult<FileSystemEntrySnapshot> existing = await _fileSystem.StatAsync(
            new FileSystemStatRequest(path), context, cancellationToken);
        if (existing.Succeeded)
        {
            return;
        }

        VirtualPath parent = GetParent(path);
        FileSystemResult<FileSystemEntrySnapshot> parentStat = await _fileSystem.StatAsync(
            new FileSystemStatRequest(parent), context, cancellationToken);
        if (!parentStat.Succeeded)
        {
            throw new InvalidOperationException(
                $"Cannot seed fixture directory '{path}': parent '{parent}' is not available ({parentStat.Error?.Code}).");
        }

        FileSystemMutationResult created = await _fileSystem.CreateAsync(
            new FileSystemCreateRequest(
                path,
                FileSystemEntryKind.Directory,
                FileSystemPermissions.FromMode(0x01C0),
                parentStat.Value!.Metadata.Revision),
            context,
            cancellationToken);
        if (!created.Succeeded && created.Transaction.Error?.Code != FileSystemErrorCode.AlreadyExists)
        {
            throw new InvalidOperationException(
                $"Cannot create fixture directory '{path}': {created.Transaction.Error?.Code}.");
        }
    }

    private async Task WriteFileAsync(
        string pathValue,
        string content,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken)
    {
        VirtualPath path = VirtualPath.Parse(pathValue);
        FileSystemResult<FileSystemEntrySnapshot> existing = await _fileSystem.StatAsync(
            new FileSystemStatRequest(path), context, cancellationToken);

        long revision;
        if (existing.Succeeded)
        {
            revision = existing.Value!.Metadata.Revision;
        }
        else
        {
            VirtualPath parent = GetParent(path);
            FileSystemResult<FileSystemEntrySnapshot> parentStat = await _fileSystem.StatAsync(
                new FileSystemStatRequest(parent), context, cancellationToken);
            if (!parentStat.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Cannot seed fixture file '{path}': parent '{parent}' is not available ({parentStat.Error?.Code}).");
            }

            FileSystemMutationResult created = await _fileSystem.CreateAsync(
                new FileSystemCreateRequest(
                    path,
                    FileSystemEntryKind.File,
                    FileSystemPermissions.FromMode(0x01A4),
                    parentStat.Value!.Metadata.Revision),
                context,
                cancellationToken);
            if (!created.Succeeded || created.Entry is null)
            {
                throw new InvalidOperationException(
                    $"Cannot create fixture file '{path}': {created.Transaction.Error?.Code}.");
            }

            revision = created.Entry.Metadata.Revision;
        }

        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        using MemoryStream stream = new(bytes);
        FixtureContentSource source = new(stream);
        FileSystemMutationResult written = await _fileSystem.WriteAsync(
            new FileSystemWriteRequest(path, revision), source, context, cancellationToken);
        if (!written.Succeeded)
        {
            throw new InvalidOperationException(
                $"Cannot write fixture file '{path}': {written.Transaction.Error?.Code}.");
        }
    }

    private static FileSystemAuthorizationContext CreateContext(string userId, string groupId)
    {
        AppOperationContext operation = new()
        {
            AppId = "org.hackeros.kernel",
            UserId = userId,
            UserAuthority = AppAuthority.System,
            GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
            IsSystemOperation = true
        };
        return new FileSystemAuthorizationContext(operation, [groupId], DateTimeOffset.UtcNow);
    }

    private static VirtualPath GetParent(VirtualPath path)
    {
        int separator = path.Value.LastIndexOf('/');
        return separator <= 0 ? VirtualPath.Parse("/") : VirtualPath.Parse(path.Value[..separator]);
    }

    private sealed class FixtureContentSource(MemoryStream stream) : IFileSystemContentSource
    {
        public FileSystemContentDescriptor Descriptor { get; } = FileSystemContentDescriptor.Text("text/plain", "utf-8");
        public long? Length { get; } = stream.Length;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            stream.Position = 0;
            return ValueTask.FromResult<Stream>(stream);
        }
    }
}
