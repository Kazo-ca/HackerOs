using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using HackerOs.App.Abstractions;

namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>Thrown when a package, its index or its contents are refused (ADR 0182 §5).</summary>
public sealed class AppPackageException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">Stable reason, e.g. <c>apps.package.hash-mismatch</c>.</param>
    /// <param name="message">Human-readable reason shown by <c>hackpkg</c>.</param>
    public AppPackageException(string code, string message)
        : base(message) =>
        Code = code;

    /// <summary>Gets the stable reason.</summary>
    public string Code { get; }
}

/// <summary>
/// A verified HackerOS app package (<c>.hackapp</c>, ADR 0182 §1): a zip archive holding
/// <c>hackeros-package.json</c>, <c>manifest.json</c>, <c>lib/*.dll</c> and <c>wwwroot/...</c>,
/// where every file matches the SHA-256 the package lists.
/// </summary>
public sealed class AppPackage
{
    /// <summary>File extension of a package.</summary>
    public const string Extension = ".hackapp";

    /// <summary>Name of the package description at the archive root.</summary>
    public const string DescriptionFileName = "hackeros-package.json";

    /// <summary>Name of the app manifest at the archive root.</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>Folder of the app assemblies.</summary>
    public const string LibraryFolder = "lib/";

    /// <summary>Folder of the assets the manifest declares.</summary>
    public const string AssetFolder = "wwwroot/";

    /// <summary>Largest archive accepted, in bytes.</summary>
    public const long MaximumArchiveBytes = 64L * 1024 * 1024;

    /// <summary>Largest total of the extracted files, in bytes.</summary>
    public const long MaximumExtractedBytes = 128L * 1024 * 1024;

    /// <summary>Most files accepted in one archive.</summary>
    public const int MaximumFiles = 2_000;

    private AppPackage(AppManifest manifest, SemanticVersion version, IReadOnlyDictionary<string, byte[]> files)
    {
        Manifest = manifest;
        Version = version;
        Files = files;
    }

    /// <summary>Gets the app identifier.</summary>
    public string AppId => Manifest.Id;

    /// <summary>Gets the package version (equal to the manifest version).</summary>
    public SemanticVersion Version { get; }

    /// <summary>Gets the app manifest.</summary>
    public AppManifest Manifest { get; }

    /// <summary>Gets every verified file by its archive path, <see cref="DescriptionFileName"/> included.</summary>
    public IReadOnlyDictionary<string, byte[]> Files { get; }

    /// <summary>Gets the app assemblies, by file name (e.g. <c>Kazo.Webamp.dll</c>).</summary>
    public IReadOnlyDictionary<string, byte[]> Assemblies => Files
        .Where(static pair => pair.Key.StartsWith(LibraryFolder, StringComparison.Ordinal))
        .ToDictionary(static pair => pair.Key[LibraryFolder.Length..], static pair => pair.Value, StringComparer.Ordinal);

    /// <summary>Reads and verifies a package.</summary>
    /// <param name="archive">The <c>.hackapp</c> bytes.</param>
    /// <exception cref="AppPackageException">The package is malformed, incomplete, or altered.</exception>
    public static AppPackage Read(byte[] archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (archive.LongLength > MaximumArchiveBytes)
        {
            throw new AppPackageException("apps.package.too-large", $"The package is larger than {MaximumArchiveBytes / (1024 * 1024)} MiB.");
        }

        Dictionary<string, byte[]> files = Extract(archive);
        if (!files.TryGetValue(DescriptionFileName, out byte[]? descriptionBytes))
        {
            throw new AppPackageException("apps.package.no-description", $"The package has no {DescriptionFileName}.");
        }

        PackageDescription description = ReadDescription(descriptionBytes);
        foreach (string path in files.Keys.Where(static path => path != DescriptionFileName))
        {
            if (!description.Files.ContainsKey(path))
            {
                throw new AppPackageException("apps.package.unlisted-file", $"'{path}' is in the package but not in {DescriptionFileName}.");
            }
        }

        foreach ((string path, string sha256) in description.Files)
        {
            if (!files.TryGetValue(path, out byte[]? bytes))
            {
                throw new AppPackageException("apps.package.missing-file", $"'{path}' is listed in {DescriptionFileName} but missing.");
            }

            if (!string.Equals(Sha256(bytes), sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new AppPackageException("apps.package.hash-mismatch", $"'{path}' does not match its SHA-256.");
            }
        }

        AppManifest manifest = ReadManifest(files);
        if (!string.Equals(manifest.Id, description.AppId, StringComparison.Ordinal))
        {
            throw new AppPackageException("apps.package.id-mismatch", $"The package names '{description.AppId}' but its manifest is '{manifest.Id}'.");
        }

        if (!SemanticVersion.TryParse(manifest.Version, out SemanticVersion manifestVersion) || manifestVersion != description.Version)
        {
            throw new AppPackageException("apps.package.version-mismatch", $"The package is version {description.Version} but its manifest says {manifest.Version}.");
        }

        CheckContents(manifest, files);
        return new AppPackage(manifest, description.Version, files);
    }

    /// <summary>Gets the lower-case hexadecimal SHA-256 of <paramref name="bytes"/>, the form packages and manifests use.</summary>
    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Gets whether <paramref name="path"/> is a safe relative path inside a package.</summary>
    public static bool IsSafeRelativePath(string path) =>
        path.Length is > 0 and <= 260
        && !path.StartsWith('/')
        && !path.Contains('\\', StringComparison.Ordinal)
        && !path.Contains(':', StringComparison.Ordinal)
        && path.Split('/').All(static segment => segment is not ("" or "." or "..") && !segment.Any(char.IsControl));

    private static Dictionary<string, byte[]> Extract(byte[] archive)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        long total = 0;
        try
        {
            using ZipArchive zip = new(new MemoryStream(archive, writable: false), ZipArchiveMode.Read);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                if (!IsSafeRelativePath(entry.FullName))
                {
                    throw new AppPackageException("apps.package.unsafe-path", $"'{entry.FullName}' is not a safe path.");
                }

                if (files.Count >= MaximumFiles)
                {
                    throw new AppPackageException("apps.package.too-many-files", $"The package holds more than {MaximumFiles} files.");
                }

                total += entry.Length;
                if (entry.Length < 0 || total > MaximumExtractedBytes)
                {
                    throw new AppPackageException("apps.package.too-large", $"The package expands to more than {MaximumExtractedBytes / (1024 * 1024)} MiB.");
                }

                using Stream stream = entry.Open();
                using MemoryStream buffer = new();
                stream.CopyTo(buffer);
                if (buffer.Length != entry.Length)
                {
                    throw new AppPackageException("apps.package.corrupt", $"'{entry.FullName}' has an inconsistent size.");
                }

                if (!files.TryAdd(entry.FullName, buffer.ToArray()))
                {
                    throw new AppPackageException("apps.package.duplicate-file", $"'{entry.FullName}' appears twice.");
                }
            }
        }
        catch (InvalidDataException exception)
        {
            throw new AppPackageException("apps.package.corrupt", $"The package is not a valid zip archive: {exception.Message}");
        }

        return files;
    }

    private static PackageDescription ReadDescription(byte[] bytes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            string appId = root.GetProperty("appId").GetString() ?? string.Empty;
            string versionText = root.GetProperty("version").GetString() ?? string.Empty;
            if (appId.Length == 0 || !SemanticVersion.TryParse(versionText, out SemanticVersion version))
            {
                throw new AppPackageException("apps.package.bad-description", $"{DescriptionFileName} needs an appId and a version.");
            }

            Dictionary<string, string> listed = new(StringComparer.Ordinal);
            foreach (JsonProperty file in root.GetProperty("files").EnumerateObject())
            {
                string hash = file.Value.GetString() ?? string.Empty;
                if (!IsSafeRelativePath(file.Name) || file.Name == DescriptionFileName || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                {
                    throw new AppPackageException("apps.package.bad-description", $"{DescriptionFileName} lists '{file.Name}' with an invalid path or SHA-256.");
                }

                listed[file.Name] = hash;
            }

            return new PackageDescription(appId, version, listed);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AppPackageException("apps.package.bad-description", $"{DescriptionFileName} is not valid: {exception.Message}");
        }
    }

    private static AppManifest ReadManifest(Dictionary<string, byte[]> files)
    {
        if (!files.TryGetValue(ManifestFileName, out byte[]? bytes))
        {
            throw new AppPackageException("apps.package.no-manifest", $"The package has no {ManifestFileName}.");
        }

        try
        {
            return AppManifestJsonSerializer.NormalizeOptionalCollections(
                AppManifestJsonSerializer.DeserializeStrict(System.Text.Encoding.UTF8.GetString(bytes)));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            throw new AppPackageException("apps.package.bad-manifest", $"{ManifestFileName} is not a valid app manifest: {exception.Message}");
        }
    }

    private static void CheckContents(AppManifest manifest, Dictionary<string, byte[]> files)
    {
        foreach (string path in files.Keys)
        {
            bool known = path is DescriptionFileName or ManifestFileName
                || (path.StartsWith(LibraryFolder, StringComparison.Ordinal) && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && path.Count(static c => c == '/') == 1)
                || path.StartsWith(AssetFolder, StringComparison.Ordinal);
            if (!known)
            {
                throw new AppPackageException("apps.package.unexpected-file", $"'{path}' is outside manifest.json, lib/*.dll and wwwroot/.");
            }
        }

        ManifestValidationResult validation = AppManifestValidator.Validate(manifest);
        if (!validation.IsValid)
        {
            throw new AppPackageException("apps.package.bad-manifest", $"{ManifestFileName} is not valid: {string.Join("; ", validation.Errors.Select(static error => error.Message))}");
        }

        foreach (string assembly in GetDeclaredAssemblies(manifest))
        {
            if (!files.ContainsKey(LibraryFolder + assembly))
            {
                throw new AppPackageException("apps.package.missing-assembly", $"The manifest needs lib/{assembly}, which the package does not hold.");
            }
        }

        foreach (AssetManifest asset in manifest.Assets)
        {
            if (!files.TryGetValue(AssetFolder + asset.Path, out byte[]? bytes))
            {
                throw new AppPackageException("apps.package.missing-asset", $"The manifest declares the asset '{asset.Path}', which the package does not hold.");
            }

            if (!string.Equals(Sha256(bytes), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new AppPackageException("apps.package.hash-mismatch", $"The asset '{asset.Path}' does not match the SHA-256 of its manifest.");
            }
        }
    }

    /// <summary>Gets every assembly a manifest names: entry points of all platforms and components.</summary>
    /// <param name="manifest">A valid manifest.</param>
    public static IReadOnlySet<string> GetDeclaredAssemblies(AppManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        HashSet<string> assemblies = new(StringComparer.Ordinal);
        if (AppManifestPlatformSupport.Resolve(manifest) is { } platforms)
        {
            assemblies.UnionWith(platforms.EntryPointsByPlatform.Values.Select(static entryPoint => entryPoint.Assembly));
        }

        assemblies.UnionWith((manifest.Components ?? []).Select(static component => component.Assembly));
        return assemblies;
    }

    private sealed record PackageDescription(string AppId, SemanticVersion Version, IReadOnlyDictionary<string, string> Files);
}
