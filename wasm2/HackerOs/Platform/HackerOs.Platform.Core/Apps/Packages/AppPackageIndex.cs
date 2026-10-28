using System.Text.Json;
using HackerOs.App.Abstractions;

namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>One published version listed in <c>index.json</c>.</summary>
/// <param name="Version">Package version.</param>
/// <param name="Path">Package path relative to the root of the packages branch.</param>
/// <param name="Sha256">Lower-case hexadecimal SHA-256 of the package.</param>
public sealed record AppPackageIndexEntry(SemanticVersion Version, string Path, string Sha256);

/// <summary>
/// <c>index.json</c> at the root of an app repository's <c>hackeros-packages</c> branch (ADR 0182 §2):
/// <code>
/// { "appId": "org.kazo.webamp",
///   "versions": [ { "version": "1.0.0", "path": "1.0.0/org.kazo.webamp-1.0.0.hackapp", "sha256": "…" } ] }
/// </code>
/// </summary>
/// <param name="AppId">App the repository publishes.</param>
/// <param name="Versions">Published versions, newest first.</param>
public sealed record AppPackageIndex(string AppId, IReadOnlyList<AppPackageIndexEntry> Versions)
{
    /// <summary>Largest index accepted, in bytes.</summary>
    public const int MaximumBytes = 1024 * 1024;

    /// <summary>Gets the newest stable version, or the newest one when every version is a prerelease.</summary>
    public AppPackageIndexEntry? Latest =>
        Versions.FirstOrDefault(static entry => entry.Version.PreRelease is null) ?? Versions.FirstOrDefault();

    /// <summary>Gets one version, or <see langword="null"/> when it is not published.</summary>
    public AppPackageIndexEntry? Find(SemanticVersion version) =>
        Versions.FirstOrDefault(entry => entry.Version == version);

    /// <summary>Reads an index.</summary>
    /// <param name="bytes">The <c>index.json</c> bytes.</param>
    /// <exception cref="AppPackageException">The index is not valid.</exception>
    public static AppPackageIndex Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaximumBytes)
        {
            throw new AppPackageException("apps.index.too-large", "index.json is larger than 1 MiB.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            string appId = root.GetProperty("appId").GetString() ?? string.Empty;
            if (appId.Length == 0)
            {
                throw new AppPackageException("apps.index.invalid", "index.json names no appId.");
            }

            List<AppPackageIndexEntry> versions = [];
            foreach (JsonElement item in root.GetProperty("versions").EnumerateArray())
            {
                string versionText = item.GetProperty("version").GetString() ?? string.Empty;
                string path = item.GetProperty("path").GetString() ?? string.Empty;
                string sha256 = item.GetProperty("sha256").GetString() ?? string.Empty;
                if (!SemanticVersion.TryParse(versionText, out SemanticVersion version)
                    || !AppPackage.IsSafeRelativePath(path)
                    || !path.EndsWith(AppPackage.Extension, StringComparison.Ordinal)
                    || sha256.Length != 64
                    || !sha256.All(Uri.IsHexDigit))
                {
                    throw new AppPackageException("apps.index.invalid", $"index.json lists an invalid version '{versionText}'.");
                }

                if (versions.Any(entry => entry.Version == version))
                {
                    throw new AppPackageException("apps.index.invalid", $"index.json lists version {version} twice.");
                }

                versions.Add(new AppPackageIndexEntry(version, path, sha256.ToLowerInvariant()));
            }

            return new AppPackageIndex(appId, [.. versions.OrderByDescending(static entry => entry.Version)]);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AppPackageException("apps.index.invalid", $"index.json is not valid: {exception.Message}");
        }
    }
}
