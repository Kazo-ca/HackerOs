using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Platform.Core.ContextMenus;

/// <summary>
/// The entries one app registered in the file menus, as the OS read them from
/// <c>/etc/hackeros/context-menus.d/&lt;appId&gt;.json</c> (ADR 0151 §3).
/// </summary>
/// <param name="AppId">The app.</param>
/// <param name="Rank">Its installation rank: entries sharing a place follow it (ADR 0151 §4.6).</param>
/// <param name="Entries">Its entries, in declaration order.</param>
public sealed record RegisteredFileMenuApp(string AppId, int Rank, IReadOnlyList<AppContextMenuEntryManifest> Entries);

/// <summary>
/// The content of one <c>/etc/hackeros/context-menus.d/&lt;appId&gt;.json</c> file (ADR 0159 §4).
/// </summary>
/// <param name="SchemaVersion">Always 1.</param>
/// <param name="AppId">The app; must match the file name.</param>
/// <param name="AppVersion">The app's version when the file was written.</param>
/// <param name="Rank">The installation rank, kept across rewrites.</param>
/// <param name="Source">SHA-256 of the manifest's entries when written: the OS rewrites the file only when the manifest changes.</param>
/// <param name="Entries">The entries, with their titles in every language.</param>
public sealed record FileContextMenuDocument(
    int SchemaVersion,
    string AppId,
    string AppVersion,
    int Rank,
    string Source,
    IReadOnlyList<AppContextMenuEntryManifest> Entries);

/// <summary>
/// The registry of the file menu entries apps register (ADR 0151 §3, ADR 0159 §4), like the
/// <c>HKCR\*\shell</c> keys of Windows: at boot it copies the <c>contextMenus</c> of every installed
/// app's manifest to <c>/etc/hackeros/context-menus.d/&lt;appId&gt;.json</c>, removes the files of
/// apps no longer installed for the machine, then reads the folder. Drawing a menu never runs app
/// code: it only reads what this registry holds.
/// </summary>
/// <remarks>
/// <para>
/// A file is rewritten only when its app's entries changed in the manifest (a new version of the app or
/// of the OS), so an administrator's edit made with <c>sudo nano</c> stays until then. Each new file
/// takes the next installation rank; a rewrite keeps the rank. Built-in apps receive theirs in
/// catalog order (<see cref="AppCatalog.ActivationOrder"/>) at the first boot.
/// </para>
/// <para>
/// A file that cannot be read, names another app, belongs to an app without the
/// <c>shell.context-menu</c> capability or breaks a rule of ADR 0151 §5 is ignored and reported once in
/// <c>/var/log/syslog</c>. Every file operation is an audited system operation of the OS.
/// </para>
/// </remarks>
public sealed class FileContextMenuRegistry
{
    /// <summary>The folder of the files.</summary>
    public const string DirectoryPath = "/etc/hackeros/context-menus.d";

    private readonly AppCatalog _catalog;
    private readonly IAppFileSystemGateway _system;
    private readonly Func<string, bool> _isUninstalledForMachine;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<RegisteredFileMenuApp> _apps = [];

    /// <summary>Creates the registry; call <see cref="SyncAsync"/> once storage is ready.</summary>
    /// <param name="catalog">The catalog of this build: the manifests and their entries.</param>
    /// <param name="system">An audited system-operation gateway (<c>/etc</c> belongs to <c>system</c>).</param>
    /// <param name="isUninstalledForMachine">Whether an app is uninstalled for the whole machine (its file is removed); <see langword="null"/> means none is.</param>
    /// <param name="logger">Writes ignored files to <c>/var/log/syslog</c>.</param>
    public FileContextMenuRegistry(
        AppCatalog catalog,
        IAppFileSystemGateway system,
        Func<string, bool>? isUninstalledForMachine = null,
        ILogger<FileContextMenuRegistry>? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _isUninstalledForMachine = isUninstalledForMachine ?? (static _ => false);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Raised after the folder was read again.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets the registered entries by app, in installation rank order, then app id.</summary>
    public IReadOnlyList<RegisteredFileMenuApp> Apps => _apps;

    /// <summary>Gets the path of the file of <paramref name="appId"/>.</summary>
    /// <param name="appId">The app.</param>
    public static VirtualPath PathOf(string appId) => VirtualPath.Parse($"{DirectoryPath}/{appId}.json");

    /// <summary>
    /// Brings the folder in line with the installed apps' manifests, then reads it. Called at boot and
    /// after any change of the installed apps; never throws for a file problem (it is logged).
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads and writes.</param>
    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, FileContextMenuDocument?> existing = await ReadFolderAsync(cancellationToken).ConfigureAwait(false);
            int nextRank = existing.Values.Where(static document => document is not null).Select(static document => document!.Rank).DefaultIfEmpty(0).Max() + 1;

            foreach (string appId in _catalog.ActivationOrder)
            {
                AppManifest manifest = _catalog.Manifests[appId];
                if (manifest.ContextMenus is not { Count: > 0 } entries || _isUninstalledForMachine(appId))
                {
                    continue;
                }

                string source = SourceOf(entries);
                existing.TryGetValue(appId, out FileContextMenuDocument? current);
                if (current is not null && current.Source == source && current.AppVersion == manifest.Version)
                {
                    continue;
                }

                FileContextMenuDocument document = new(1, appId, manifest.Version, current?.Rank ?? nextRank++, source, entries);
                FileSystemError? error = await SettingsTextFile
                    .WriteAllAsync(_system, PathOf(appId), Serialize(document), cancellationToken)
                    .ConfigureAwait(false);
                if (error is null)
                {
                    existing[appId] = document;
                }
                else
                {
                    _logger.LogWarning("context-menu: cannot write {Path}: {Code}", PathOf(appId).Value, error.Code);
                }
            }

            foreach (string appId in existing.Keys.ToArray())
            {
                bool keep = _catalog.Manifests.TryGetValue(appId, out AppManifest? manifest)
                    && manifest.ContextMenus is { Count: > 0 }
                    && !_isUninstalledForMachine(appId);
                if (!keep && await DeleteAsync(PathOf(appId), cancellationToken).ConfigureAwait(false))
                {
                    existing.Remove(appId);
                }
            }

            _apps = Load(existing);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reads the folder again without writing anything (after an edit made by hand).</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _apps = Load(await ReadFolderAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Serializes a document as the OS writes it (indented, for <c>cat</c> and <c>nano</c>).</summary>
    /// <param name="document">The document.</param>
    public static string Serialize(FileContextMenuDocument document) =>
        JsonSerializer.Serialize(document, FileContextMenuJsonContext.Default.FileContextMenuDocument) + "\n";

    /// <summary>Reads a document, or <see langword="null"/> when it is not valid JSON of the expected shape.</summary>
    /// <param name="json">The file's text.</param>
    public static FileContextMenuDocument? TryDeserialize(string json)
    {
        try
        {
            FileContextMenuDocument? document = JsonSerializer.Deserialize(json, FileContextMenuJsonContext.Default.FileContextMenuDocument);
            return document is { SchemaVersion: 1, AppId.Length: > 0, Entries: not null } ? document : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The fingerprint of an app's entries, to rewrite its file only when they change.</summary>
    /// <param name="entries">The manifest's entries.</param>
    public static string SourceOf(IReadOnlyList<AppContextMenuEntryManifest> entries)
    {
        string json = JsonSerializer.Serialize(entries, FileContextMenuJsonContext.Default.IReadOnlyListAppContextMenuEntryManifest);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private IReadOnlyList<RegisteredFileMenuApp> Load(IReadOnlyDictionary<string, FileContextMenuDocument?> documents)
    {
        List<RegisteredFileMenuApp> apps = [];
        foreach ((string appId, FileContextMenuDocument? document) in documents)
        {
            string path = PathOf(appId).Value;
            if (document is null)
            {
                _logger.LogWarning("context-menu: {Path} cannot be read; its entries are ignored", path);
                continue;
            }

            if (document.AppId != appId)
            {
                _logger.LogWarning("context-menu: {Path} names the app '{AppId}'; its entries are ignored", path, document.AppId);
                continue;
            }

            if (!_catalog.Manifests.TryGetValue(appId, out AppManifest? manifest))
            {
                continue;
            }

            // ADR 0151 §6: without the capability the OS ignores the entries, whatever the file says.
            if (!manifest.Capabilities.Contains(AppCapabilities.ShellContextMenu, StringComparer.Ordinal))
            {
                _logger.LogWarning("context-menu: {AppId} does not request {Capability}; its entries are ignored", appId, AppCapabilities.ShellContextMenu);
                continue;
            }

            ManifestValidationError? error = AppManifestValidator.Validate(manifest with { ContextMenus = document.Entries }).Errors
                .FirstOrDefault(static item => item.Code.StartsWith("manifest.context-menu.", StringComparison.Ordinal));
            if (error is not null)
            {
                _logger.LogWarning("context-menu: {Path} is invalid ({Code}: {Message}); its entries are ignored", path, error.Code, error.Message);
                continue;
            }

            apps.Add(new RegisteredFileMenuApp(appId, document.Rank, document.Entries));
        }

        return [.. apps.OrderBy(static app => app.Rank).ThenBy(static app => app.AppId, StringComparer.Ordinal)];
    }

    private async Task<Dictionary<string, FileContextMenuDocument?>> ReadFolderAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, FileContextMenuDocument?> documents = new(StringComparer.Ordinal);
        FileSystemResult<FileSystemDirectorySnapshot> listing = await _system
            .EnumerateAsync(new FileSystemEnumerateRequest(VirtualPath.Parse(DirectoryPath)), cancellationToken)
            .ConfigureAwait(false);
        if (!listing.Succeeded || listing.Value is null)
        {
            // A fresh machine has no folder yet: the first sync creates it with the first file.
            return documents;
        }

        foreach (FileSystemDirectoryItem item in listing.Value.Entries)
        {
            string name = item.Name.Value;
            if (item.Metadata.Kind != FileSystemEntryKind.File || !name.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }

            SettingsFileReadResult read = await SettingsTextFile
                .ReadAsync(_system, VirtualPath.Parse($"{DirectoryPath}/{name}"), cancellationToken)
                .ConfigureAwait(false);
            documents[name[..^".json".Length]] = read.Content is { } content ? TryDeserialize(content) : null;
        }

        return documents;
    }

    private async Task<bool> DeleteAsync(VirtualPath path, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemEntrySnapshot> entry = await _system.StatAsync(new FileSystemStatRequest(path), cancellationToken).ConfigureAwait(false);
        FileSystemResult<FileSystemEntrySnapshot> parent = await _system
            .StatAsync(new FileSystemStatRequest(SettingsTextFile.GetParent(path)), cancellationToken)
            .ConfigureAwait(false);
        if (!entry.Succeeded || !parent.Succeeded)
        {
            return !entry.Succeeded;
        }

        FileSystemMutationResult deleted = await _system
            .DeleteAsync(new FileSystemDeleteRequest(path, entry.Value!.Metadata.Revision, parent.Value!.Metadata.Revision), cancellationToken)
            .ConfigureAwait(false);
        if (!deleted.Succeeded)
        {
            _logger.LogWarning("context-menu: cannot remove {Path}: {Code}", path.Value, deleted.Transaction.Error?.Code);
        }

        return deleted.Succeeded;
    }
}

/// <summary>Source-generated (trim-safe) JSON metadata of <see cref="FileContextMenuDocument"/>.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(FileContextMenuDocument))]
[JsonSerializable(typeof(IReadOnlyList<AppContextMenuEntryManifest>))]
internal sealed partial class FileContextMenuJsonContext : JsonSerializerContext
{
}
