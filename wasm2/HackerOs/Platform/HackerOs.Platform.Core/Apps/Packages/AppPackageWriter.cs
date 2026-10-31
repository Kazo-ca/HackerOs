using System.IO.Compression;
using System.Text;
using System.Text.Json;
using HackerOs.App.Abstractions;

namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>
/// Writes a <c>.hackapp</c> package and its <c>index.json</c> entry (ADR 0182 §1, §2): the other half
/// of <see cref="AppPackage.Read"/>, run by the <c>HackerOsPack</c> target of an app project.
/// </summary>
public static class AppPackageWriter
{
    /// <summary>Builds the package of one app, without satellite assemblies.</summary>
    /// <param name="manifestJson">The generated <c>manifest.json</c>, byte for byte.</param>
    /// <param name="readAssembly">Reads one declared assembly (<c>Kazo.Webamp.dll</c>) from the build output.</param>
    /// <param name="readAsset">Reads one declared asset (path relative to <c>wwwroot/</c>).</param>
    /// <returns>The package, already checked with <see cref="AppPackage.Read"/>.</returns>
    /// <exception cref="AppPackageException">The result would be refused by the OS.</exception>
    public static byte[] Write(byte[] manifestJson, Func<string, byte[]> readAssembly, Func<string, byte[]> readAsset) =>
        Write(manifestJson, readAssembly, readAsset, null);

    /// <summary>Builds the package of one app.</summary>
    /// <param name="manifestJson">The generated <c>manifest.json</c>, byte for byte.</param>
    /// <param name="readAssembly">Reads one declared assembly (<c>Kazo.Webamp.dll</c>) from the build output.</param>
    /// <param name="readAsset">Reads one declared asset (path relative to <c>wwwroot/</c>).</param>
    /// <param name="satellites">
    /// The satellite assemblies of the app's assemblies (ADR 0222 §1), by their path under <c>lib/</c>
    /// (<c>fr/Kazo.Webamp.resources.dll</c>); <see langword="null"/> for none.
    /// </param>
    /// <returns>The package, already checked with <see cref="AppPackage.Read"/>.</returns>
    /// <exception cref="AppPackageException">The result would be refused by the OS.</exception>
    public static byte[] Write(
        byte[] manifestJson,
        Func<string, byte[]> readAssembly,
        Func<string, byte[]> readAsset,
        IReadOnlyDictionary<string, byte[]>? satellites)
    {
        ArgumentNullException.ThrowIfNull(manifestJson);
        ArgumentNullException.ThrowIfNull(readAssembly);
        ArgumentNullException.ThrowIfNull(readAsset);
        AppManifest manifest;
        try
        {
            manifest = AppManifestJsonSerializer.NormalizeOptionalCollections(
                AppManifestJsonSerializer.DeserializeStrict(Encoding.UTF8.GetString(manifestJson)));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            throw new AppPackageException("apps.package.bad-manifest", $"manifest.json is not valid: {exception.Message}");
        }

        SortedDictionary<string, byte[]> files = new(StringComparer.Ordinal) { [AppPackage.ManifestFileName] = manifestJson };
        foreach (string assembly in AppPackage.GetDeclaredAssemblies(manifest))
        {
            files[AppPackage.LibraryFolder + assembly] = readAssembly(assembly);
        }

        foreach (AssetManifest asset in manifest.Assets ?? [])
        {
            files[AppPackage.AssetFolder + asset.Path] = readAsset(asset.Path);
        }

        foreach ((string path, byte[] bytes) in satellites ?? new Dictionary<string, byte[]>())
        {
            files[AppPackage.LibraryFolder + path] = bytes;
        }

        byte[] description = WriteJson(writer =>
        {
            writer.WriteString("appId", manifest.Id);
            writer.WriteString("version", manifest.Version);
            writer.WriteStartObject("files");
            foreach ((string path, byte[] bytes) in files)
            {
                writer.WriteString(path, AppPackage.Sha256(bytes));
            }

            writer.WriteEndObject();
        });

        using MemoryStream archive = new();
        using (ZipArchive zip = new(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, byte[] bytes) in files.Prepend(new(AppPackage.DescriptionFileName, description)))
            {
                // A fixed date: the same inputs give the same package, so its SHA-256 is reproducible.
                ZipArchiveEntry entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using Stream stream = entry.Open();
                stream.Write(bytes);
            }
        }

        byte[] package = archive.ToArray();
        AppPackage.Read(package);
        return package;
    }

    /// <summary>Gets the path of a package inside the packages branch: <c>&lt;version&gt;/&lt;appId&gt;-&lt;version&gt;.hackapp</c>.</summary>
    /// <param name="appId">App identifier.</param>
    /// <param name="version">Package version.</param>
    public static string GetPackagePath(string appId, SemanticVersion version) =>
        $"{version}/{appId}-{version}{AppPackage.Extension}";

    /// <summary>
    /// Adds a version to <c>index.json</c>, or replaces it when it is already listed; versions stay
    /// newest first and keep their <c>signature</c> (ADR 0219 §2).
    /// </summary>
    /// <param name="existing">The current index, or <see langword="null"/> for a first publication.</param>
    /// <param name="appId">App identifier; it must match the existing index.</param>
    /// <param name="entry">The new version.</param>
    /// <returns>The new <c>index.json</c> bytes.</returns>
    /// <exception cref="AppPackageException">The existing index is invalid or names another app.</exception>
    public static byte[] AddToIndex(byte[]? existing, string appId, AppPackageIndexEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentNullException.ThrowIfNull(entry);
        AppPackageIndex index = existing is null ? new AppPackageIndex(appId, []) : AppPackageIndex.Parse(existing);
        if (!string.Equals(index.AppId, appId, StringComparison.Ordinal))
        {
            throw new AppPackageException("apps.index.invalid", $"index.json publishes {index.AppId}, not {appId}.");
        }

        IEnumerable<AppPackageIndexEntry> versions = index.Versions
            .Where(current => current.Version != entry.Version)
            .Append(entry)
            .OrderByDescending(static current => current.Version);
        return WriteJson(writer =>
        {
            writer.WriteString("appId", appId);
            writer.WriteStartArray("versions");
            foreach (AppPackageIndexEntry current in versions)
            {
                writer.WriteStartObject();
                writer.WriteString("version", current.Version.ToString());
                writer.WriteString("path", current.Path);
                writer.WriteString("sha256", current.Sha256);
                // ADR 0219 §2: the signatures already published are kept; a replaced version has the new entry's.
                if (current.Signature is { } signature)
                {
                    writer.WriteString(AppPackageSignature.FieldName, Convert.ToBase64String(signature));
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    private static byte[] WriteJson(Action<Utf8JsonWriter> writeProperties)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }
}
