using System.Text.Json;

namespace HackerOs.TrimmedPublish.E2E.Tests;

/// <summary>
/// What <c>build/publish-trimmed.sh</c> tells the suite about the publish under test, through
/// environment variables (the only channel <c>dotnet test</c> passes through unchanged).
/// </summary>
/// <param name="Url">Base address of the served publish, e.g. <c>http://127.0.0.1:5123</c>.</param>
/// <param name="CatalogPath">
/// The host's <c>BuiltInApplications.json</c> (ADR 0047), from which the suite takes the window
/// apps to launch, or <see langword="null"/> to use <see cref="FallbackWindowApps"/>.
/// </param>
/// <param name="ArtifactsDirectory">Where a failing run leaves its screenshots, or <see langword="null"/>.</param>
/// <param name="PwaUrl">
/// The same publish on an address where <c>index.html</c> registers the service worker (it skips
/// <c>localhost</c> and <c>127.0.0.1</c>), e.g. <c>http://127.0.0.2:5124</c>; <see langword="null"/>
/// skips the offline check.
/// </param>
/// <param name="PackagesDirectory">
/// The <c>hackeros-packages</c> folder of the test package of ADR 0206 (<c>index.json</c> and its
/// <c>.hackapp</c>), served to the browser in place of GitHub; <see langword="null"/> skips that test.
/// </param>
public sealed record TrimmedPublishSettings(string Url, string? CatalogPath, string? ArtifactsDirectory, string? PwaUrl = null, string? PackagesDirectory = null)
{
    /// <summary>Base address of the served publish. Required.</summary>
    public const string UrlVariable = "HACKEROS_TRIMMED_URL";

    /// <summary>Path of the host's <c>BuiltInApplications.json</c>.</summary>
    public const string CatalogVariable = "HACKEROS_TRIMMED_CATALOG";

    /// <summary>Directory for failure screenshots.</summary>
    public const string ArtifactsVariable = "HACKEROS_TRIMMED_ARTIFACTS";

    /// <summary>Address of the same publish with the service worker active.</summary>
    public const string PwaUrlVariable = "HACKEROS_TRIMMED_PWA_URL";

    /// <summary>Folder of the test package installed by hackpkg (ADR 0206).</summary>
    public const string PackagesVariable = "HACKEROS_TRIMMED_PACKAGES";

    /// <summary>The window apps of the Ecosystem host, when no catalog is given.</summary>
    public static readonly IReadOnlyList<string> FallbackWindowApps =
    [
        "Browser", "Calculator", "Code Editor", "Error Log Viewer", "File Explorer", "Hack Paint",
        "Icon Viewer", "IndexedDB Diagnostic", "Settings", "System Monitor", "Terminal", "Text Editor",
        "Theme Editor", "Web Server Manager",
    ];

    /// <summary>Reads the settings, or returns <see langword="null"/> when no URL is set.</summary>
    public static TrimmedPublishSettings? FromEnvironment()
    {
        string? url = Read(UrlVariable);
        return url is null
            ? null
            : new TrimmedPublishSettings(url.TrimEnd('/'), Read(CatalogVariable), Read(ArtifactsVariable), Read(PwaUrlVariable)?.TrimEnd('/'), Read(PackagesVariable));
    }

    /// <summary>
    /// The display names of the window apps the Start menu shows (kind <c>window</c>, launch
    /// visibility <c>visible</c>): every one of them is reached only by name from a lazy-loaded
    /// assembly, which is what a trimmed publish can break.
    /// </summary>
    public IReadOnlyList<string> WindowApps()
    {
        if (CatalogPath is null)
        {
            return FallbackWindowApps;
        }

        using JsonDocument catalog = JsonDocument.Parse(File.ReadAllText(CatalogPath));
        List<string> names = [];
        foreach (JsonElement application in catalog.RootElement.GetProperty("applications").EnumerateArray())
        {
            JsonElement manifest = application.GetProperty("manifest");
            bool visible = !manifest.TryGetProperty("presentation", out JsonElement presentation)
                || !presentation.TryGetProperty("launchVisibility", out JsonElement visibility)
                || visibility.GetString() == "visible";
            if (manifest.GetProperty("kind").GetString() == "window" && visible)
            {
                names.Add(manifest.GetProperty("name").GetString()!);
            }
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static string? Read(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
