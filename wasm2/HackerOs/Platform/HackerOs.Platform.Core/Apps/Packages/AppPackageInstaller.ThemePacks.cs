using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Apps.ThemePacks;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Theming.Abstractions;
using Microsoft.Extensions.Logging;

namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>A downloaded and verified theme pack, ready to be written (ADR 0213 §4).</summary>
/// <param name="Source">Its catalog source, pinned to the pack's version.</param>
/// <param name="Pack">The verified pack.</param>
/// <param name="Trust">Trust level of the catalog.</param>
/// <param name="PreviousVersion">The version it replaces in the same place, or <see langword="null"/>.</param>
public sealed record ThemePackPlan(AppPackageSource Source, ThemePack Pack, AppSourceTrust Trust, SemanticVersion? PreviousVersion)
{
    /// <summary>Gets the home of the user it is installed for, or <see langword="null"/> for every user.</summary>
    public string? Home { get; init; }

    /// <summary>Gets whether it is installed for every user.</summary>
    public bool SystemWide => Home is null;

    /// <summary>Gets the block-list rule that matches the pack, if any (ADR 0189 §5).</summary>
    public AppBlockReason? BlockReason { get; init; }

    /// <summary>Gets whether the two-step approval of ADR 0189 §4 is needed: for the system, or from an untrusted catalog.</summary>
    public bool RequiresAdministrator => SystemWide || Trust == AppSourceTrust.Untrusted;
}

/// <summary>A theme pack found in a pack folder (ADR 0213 §3), as its install record describes it.</summary>
/// <param name="Id">Pack id (its folder name).</param>
/// <param name="Name">Its name.</param>
/// <param name="Version">Installed version.</param>
/// <param name="Source">Where it came from, such as <c>catalog:official/org.example.neon@1.0.0</c>.</param>
/// <param name="SystemWide">Whether it is in <c>/usr/share/hackeros/theme-packs</c>.</param>
/// <param name="Parts">What it holds.</param>
public sealed record InstalledThemePack(string Id, string Name, SemanticVersion Version, string Source, bool SystemWide, ThemePackParts Parts);

/// <summary>
/// Theme packs of the Hack Store (ADR 0190, ADR 0213): a <c>.hacktheme</c> listed with
/// <c>kind: "theme-pack"</c> in a catalog is downloaded, checked against the SHA-256 of the
/// catalog, verified file by file (<see cref="ThemePack.Read"/>), then unpacked as it is in
/// <see cref="ThemePackLayout.SystemDirectory"/> or the user's <c>~/.local/share/hackeros/theme-packs</c>.
/// </summary>
/// <remarks>
/// <para>
/// The pack is first written to a staging folder next to the packs (<c>theme-packs.partial/</c>),
/// then renamed into place in one move, the way a package manager unpacks then renames: the
/// catalogs that watch the packs folder never see half a pack, and an interrupted install leaves
/// the previous version where it was.
/// </para>
/// <para>
/// Each installed folder holds an install record, <see cref="InstallRecordFileName"/>, like the
/// status file of <c>dpkg</c>: id, name, version, source, parts and who installed it.
/// </para>
/// </remarks>
public sealed partial class AppPackageInstaller
{
    /// <summary>Name of the install record written in each installed pack folder.</summary>
    public const string InstallRecordFileName = ".hackeros-install";

    private const string StagingSuffix = ".partial";

    /// <summary>
    /// Finds the catalog that publishes a theme pack: the first one of
    /// <see cref="GetCatalogSearchOrder"/> that lists it (ADR 0212 §4, applied to packs).
    /// </summary>
    /// <param name="packId">The pack.</param>
    /// <param name="version">A version, or <see langword="null"/> for the newest stable one.</param>
    /// <param name="sources">The sources.</param>
    /// <param name="cancellationToken">Cancels the downloads.</param>
    /// <exception cref="AppPackageException"><c>themes.pack.not-in-catalog</c>.</exception>
    public async ValueTask<AppPackageSource> FindThemePackInCatalogsAsync(
        string packId, SemanticVersion? version, AppSourcesConfig sources, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentNullException.ThrowIfNull(sources);
        foreach (string name in GetCatalogSearchOrder(sources))
        {
            try
            {
                AppCatalogListing listing = await GetCatalogAsync(name, sources, cancellationToken).ConfigureAwait(false);
                if (listing.Find(packId, AppCatalogEntryKind.ThemePack) is not null)
                {
                    return AppPackageSource.ForCatalog(name, packId, version);
                }
            }
            catch (AppPackageException)
            {
                // An unreachable catalog is skipped, as for apps.
            }
        }

        throw new AppPackageException("themes.pack.not-in-catalog", $"The theme pack {packId} is in none of the catalogs.");
    }

    /// <summary>
    /// Downloads and verifies a pack and decides for whom it is installed (rules of ADR 0189 §3 to
    /// §5, as for an app). Nothing is written.
    /// </summary>
    /// <param name="source">A catalog source (<c>catalog:&lt;name&gt;/&lt;packId&gt;</c>), with or without a version.</param>
    /// <param name="sources">The sources.</param>
    /// <param name="policy">The administrator's block list.</param>
    /// <param name="scope">For whom the caller asked.</param>
    /// <param name="home">The session user's home, or <see langword="null"/> without a session.</param>
    /// <param name="systemInstalled">Packs installed for every user.</param>
    /// <param name="personalInstalled">Packs installed for the session user.</param>
    /// <param name="cancellationToken">Cancels the downloads.</param>
    /// <exception cref="AppPackageException">The pack is refused; the code says why.</exception>
    public async ValueTask<ThemePackPlan> PrepareThemePackAsync(
        AppPackageSource source,
        AppSourcesConfig sources,
        AppPolicy policy,
        AppPackageScope scope,
        string? home,
        IReadOnlyList<InstalledThemePack> systemInstalled,
        IReadOnlyList<InstalledThemePack> personalInstalled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(systemInstalled);
        ArgumentNullException.ThrowIfNull(personalInstalled);
        if (!source.IsCatalog)
        {
            throw new AppPackageException("themes.pack.bad-source", $"{source.Name} is not a catalog: theme packs come from a catalog (catalog:<name>/<packId>).");
        }

        if (scope == AppPackageScope.User && home is null)
        {
            throw new AppPackageException("apps.package.no-session", "A personal install needs a signed-in user.");
        }

        AppCatalogListing listing = await GetCatalogAsync(source.Catalog!, sources, cancellationToken).ConfigureAwait(false);
        AppCatalogEntry entry = listing.Find(source.CatalogAppId!, AppCatalogEntryKind.ThemePack)
            ?? throw new AppPackageException("themes.pack.not-in-catalog", $"The catalog '{source.Catalog}' lists no theme pack {source.CatalogAppId}.");
        AppCatalogVersion version = (source.Version is { } wanted
                ? entry.Versions.FirstOrDefault(candidate => candidate.Version == wanted)
                : entry.Latest)
            ?? throw new AppPackageException("apps.package.version-not-found", $"{source.Name} has no version {source.Version}.");
        byte[] archive = await _downloader
            .DownloadAsync(listing.Resolve(version.Package), ThemePack.MaximumBytes, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new AppPackageException("apps.package.download-failed", $"{version.Package} is listed by the catalog '{source.Catalog}' but missing.");
        if (!string.Equals(AppPackage.Sha256(archive), version.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppPackageException("apps.package.hash-mismatch", $"{version.Package} does not match the SHA-256 of the catalog '{source.Catalog}'.");
        }

        ThemePack pack = ThemePack.Read(archive);
        if (!string.Equals(pack.Id, entry.AppId, StringComparison.Ordinal) || pack.Version != version.Version)
        {
            throw new AppPackageException(
                "apps.package.id-mismatch", $"{version.Package} holds {pack.Id} {pack.Version}, the catalog announces {entry.AppId} {version.Version}.");
        }

        AppSourceTrust trust = sources.GetTrust(source);
        AppBlockReason? blocked = policy.FindBlock(pack.Id, entry.Category);
        bool forSystem = systemInstalled.Any(installed => installed.Id == pack.Id);
        bool forUser = personalInstalled.Any(installed => installed.Id == pack.Id);
        bool systemWide = scope switch
        {
            AppPackageScope.System => true,
            AppPackageScope.User when forSystem => throw new AppPackageException(
                "apps.package.installed-for-system", $"{pack.Id} is installed for every user; one copy at a time."),
            AppPackageScope.User when blocked is not null => throw new AppPackageException(
                "apps.package.blocked", $"{pack.Id} is blocked for a personal install by {AppPolicyConfigDocument.MachinePath.Value} ({blocked})."),
            AppPackageScope.User => false,
            _ => home is null || forSystem || blocked is not null || (!forUser && trust == AppSourceTrust.Untrusted)
        };
        InstalledThemePack? previous = (systemWide ? systemInstalled : personalInstalled).FirstOrDefault(installed => installed.Id == pack.Id);
        return new ThemePackPlan(source.WithVersion(pack.Version), pack, trust, previous?.Version)
        {
            Home = systemWide ? null : home,
            BlockReason = blocked,
        };
    }

    /// <summary>
    /// Writes a verified pack: staged next to the packs, then renamed into place over the previous
    /// version, which is deleted last. A system install also removes the personal copies of the
    /// same pack in <paramref name="personalHomes"/> (one copy at a time, ADR 0189 §3).
    /// </summary>
    /// <param name="writer">The system writer after the approval, or the user's own writer.</param>
    /// <param name="plan">The verified pack.</param>
    /// <param name="by">Login name of the user or administrator, for the log.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <param name="personalHomes">Homes whose personal copy a system install replaces.</param>
    /// <exception cref="IOException">A write failed; the previous version stays installed.</exception>
    public async ValueTask CommitThemePackAsync(
        IAppFileSystemGateway writer,
        ThemePackPlan plan,
        string by,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? personalHomes = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(plan);
        ThemePack pack = plan.Pack;
        string root = plan.Home is null ? ThemePackLayout.SystemDirectory : ThemePackLayout.GetUserDirectory(plan.Home);
        VirtualPath staging = VirtualPath.Parse($"{root}{StagingSuffix}/{pack.Id}");
        VirtualPath retired = VirtualPath.Parse($"{root}{StagingSuffix}/{pack.Id}.old");
        VirtualPath target = VirtualPath.Parse($"{root}/{pack.Id}");
        await DeleteIfExistsAsync(writer, staging, cancellationToken).ConfigureAwait(false);
        await DeleteIfExistsAsync(writer, retired, cancellationToken).ConfigureAwait(false);
        Ensure(await SettingsTextFile.EnsureDirectoryAsync(writer, staging, cancellationToken).ConfigureAwait(false));
        foreach ((string relativePath, byte[] content) in pack.Files.OrderBy(static file => file.Key, StringComparer.Ordinal))
        {
            await WriteFileAsync(writer, VirtualPath.Parse($"{staging.Value}/{relativePath}"), content, cancellationToken).ConfigureAwait(false);
        }

        await WriteFileAsync(
                writer,
                VirtualPath.Parse($"{staging.Value}/{InstallRecordFileName}"),
                System.Text.Encoding.UTF8.GetBytes(FormatInstallRecord(pack, plan.Source, by)),
                cancellationToken)
            .ConfigureAwait(false);
        Ensure(await SettingsTextFile.EnsureDirectoryAsync(writer, VirtualPath.Parse(root), cancellationToken).ConfigureAwait(false));
        if (await ExistsAsync(writer, target, cancellationToken).ConfigureAwait(false))
        {
            await MoveAsync(writer, target, retired, cancellationToken).ConfigureAwait(false);
        }

        await MoveAsync(writer, staging, target, cancellationToken).ConfigureAwait(false);
        await DeleteIfExistsAsync(writer, retired, cancellationToken).ConfigureAwait(false);
        if (plan.Home is null)
        {
            foreach (string home in personalHomes ?? [])
            {
                await DeleteIfExistsAsync(writer, VirtualPath.Parse($"{ThemePackLayout.GetUserDirectory(home)}/{pack.Id}"), cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("themes: pack {PackId} personal copy of {Home} removed, now installed for the system", pack.Id, home);
            }
        }

        _logger.LogInformation(
            "themes: pack {PackId} {Version} installed for {Scope} from {Source} (parts={Parts}, trust={Trust}, by={User})",
            pack.Id, pack.Version, plan.Home ?? "the system", plan.Source.Name, pack.Parts, plan.Trust, by);
    }

    /// <summary>
    /// Lists the packs installed in one place: each folder of <paramref name="root"/> whose install
    /// record is valid and names that folder. A folder copied by hand without a record is still read
    /// by the catalogs, but is not a Store install.
    /// </summary>
    /// <param name="reader">Reads as the system or as the user.</param>
    /// <param name="root"><see cref="ThemePackLayout.SystemDirectory"/> or a user's pack folder.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async ValueTask<IReadOnlyList<InstalledThemePack>> ReadInstalledThemePacksAsync(
        IAppFileSystemGateway reader, string root, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        FileSystemResult<FileSystemDirectorySnapshot> listed = await reader
            .EnumerateAsync(new FileSystemEnumerateRequest(VirtualPath.Parse(root)), cancellationToken)
            .ConfigureAwait(false);
        if (!listed.Succeeded || listed.Value is null)
        {
            return [];
        }

        bool systemWide = root == ThemePackLayout.SystemDirectory;
        List<InstalledThemePack> packs = [];
        foreach (FileSystemDirectoryItem folder in listed.Value.Entries
            .Where(static entry => entry.Metadata.Kind == FileSystemEntryKind.Directory)
            .OrderBy(static entry => entry.Name.Value, StringComparer.Ordinal))
        {
            SettingsFileReadResult record = await SettingsTextFile
                .ReadAsync(reader, VirtualPath.Parse($"{root}/{folder.Name.Value}/{InstallRecordFileName}"), cancellationToken)
                .ConfigureAwait(false);
            if (ParseInstallRecord(record.Content, systemWide) is { } pack && pack.Id == folder.Name.Value)
            {
                packs.Add(pack);
            }
        }

        return packs;
    }

    /// <summary>Finds the homes that hold a personal copy of a pack, which a system install replaces.</summary>
    /// <param name="system">The system reader.</param>
    /// <param name="packId">The pack.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async ValueTask<IReadOnlyList<string>> FindPersonalThemePackCopiesAsync(
        IAppFileSystemGateway system, string packId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        FileSystemResult<FileSystemDirectorySnapshot> homes = await system
            .EnumerateAsync(new FileSystemEnumerateRequest(VirtualPath.Parse(InstalledAppsLayout.HomesRoot)), cancellationToken)
            .ConfigureAwait(false);
        List<string> found = [];
        foreach (FileSystemDirectoryItem entry in homes.Succeeded ? homes.Value!.Entries : [])
        {
            string home = $"{InstalledAppsLayout.HomesRoot}/{entry.Name.Value}";
            if (entry.Metadata.Kind == FileSystemEntryKind.Directory
                && (await ReadInstalledThemePacksAsync(system, ThemePackLayout.GetUserDirectory(home), cancellationToken).ConfigureAwait(false))
                    .Any(pack => pack.Id == packId))
            {
                found.Add(home);
            }
        }

        return found;
    }

    /// <summary>Removes an installed pack: its whole folder. What it offered leaves the catalogs at once.</summary>
    /// <param name="writer">The system writer after the approval, or the user's own writer.</param>
    /// <param name="packId">The installed pack.</param>
    /// <param name="by">Login name, for the log.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <param name="home">The user's home for a personal pack, or <see langword="null"/> for the system.</param>
    /// <returns>What was removed.</returns>
    /// <exception cref="AppPackageException"><c>themes.pack.not-installed</c>.</exception>
    public async ValueTask<InstalledThemePack> RemoveThemePackAsync(
        IAppFileSystemGateway writer, string packId, string by, CancellationToken cancellationToken = default, string? home = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        string root = home is null ? ThemePackLayout.SystemDirectory : ThemePackLayout.GetUserDirectory(home);
        InstalledThemePack installed = (await ReadInstalledThemePacksAsync(writer, root, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(pack => pack.Id == packId)
            ?? throw new AppPackageException(
                "themes.pack.not-installed",
                home is null ? $"The theme pack {packId} is not installed for the system." : $"The theme pack {packId} is not installed for you.");
        await DeleteIfExistsAsync(writer, VirtualPath.Parse($"{root}/{packId}"), cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "themes: pack {PackId} {Version} removed for {Scope}, installed from {Source} (by={User})",
            packId, installed.Version, home ?? "the system", installed.Source, by);
        return installed;
    }

    /// <summary>Writes the install record of a pack (INI-like <c>key=value</c> lines).</summary>
    internal static string FormatInstallRecord(ThemePack pack, AppPackageSource source, string by) =>
        "# Written by the Hack Store when it installed this theme pack (ADR 0213). Removing the pack removes this folder.\n"
        + $"id={pack.Id}\n"
        + $"name={OneLine(pack.Name)}\n"
        + $"version={pack.Version}\n"
        + $"source={source}\n"
        + $"parts={FormatParts(pack.Parts)}\n"
        + $"installed-by={OneLine(by)}\n";

    /// <summary>Reads an install record; <see langword="null"/> when it is missing or invalid.</summary>
    internal static InstalledThemePack? ParseInstallRecord(string? content, bool systemWide)
    {
        if (content is null)
        {
            return null;
        }

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string line in content.Split('\n'))
        {
            string trimmed = line.Trim();
            int equals = trimmed.IndexOf('=');
            if (trimmed.Length == 0 || trimmed[0] == '#' || equals <= 0)
            {
                continue;
            }

            values[trimmed[..equals].Trim()] = trimmed[(equals + 1)..].Trim();
        }

        return values.TryGetValue("id", out string? id) && ThemePack.IsValidId(id)
            && values.TryGetValue("version", out string? versionText) && SemanticVersion.TryParse(versionText, out SemanticVersion version)
            ? new InstalledThemePack(
                id,
                values.GetValueOrDefault("name") is { Length: > 0 } name ? name : id,
                version,
                values.GetValueOrDefault("source") ?? string.Empty,
                systemWide,
                ParseParts(values.GetValueOrDefault("parts")))
            : null;
    }

    private static string FormatParts(ThemePackParts parts) =>
        string.Join(',', Enum.GetValues<ThemePackParts>().Where(part => part != ThemePackParts.None && parts.HasFlag(part)).Select(static part => part.ToString().ToLowerInvariant()));

    private static ThemePackParts ParseParts(string? text) =>
        (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Aggregate(ThemePackParts.None, static (parts, name) => Enum.TryParse(name, ignoreCase: true, out ThemePackParts part) ? parts | part : parts);

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    private static async ValueTask<bool> ExistsAsync(IAppFileSystemGateway system, VirtualPath path, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemEntrySnapshot> entry = await system.StatAsync(new FileSystemStatRequest(path), cancellationToken).ConfigureAwait(false);
        Ensure(entry.Succeeded || entry.Error?.Code == FileSystemErrorCode.NotFound ? null : entry.Error);
        return entry.Succeeded;
    }

    /// <summary>Renames a folder in one move, with the revisions the filesystem asks for.</summary>
    private static async ValueTask MoveAsync(IAppFileSystemGateway system, VirtualPath source, VirtualPath destination, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemEntrySnapshot> entry = await system.StatAsync(new FileSystemStatRequest(source), cancellationToken).ConfigureAwait(false);
        Ensure(entry.Succeeded ? null : entry.Error);
        FileSystemResult<FileSystemEntrySnapshot> sourceParent = await system
            .StatAsync(new FileSystemStatRequest(SettingsTextFile.GetParent(source)), cancellationToken).ConfigureAwait(false);
        Ensure(sourceParent.Succeeded ? null : sourceParent.Error);
        FileSystemResult<FileSystemEntrySnapshot> destinationParent = await system
            .StatAsync(new FileSystemStatRequest(SettingsTextFile.GetParent(destination)), cancellationToken).ConfigureAwait(false);
        Ensure(destinationParent.Succeeded ? null : destinationParent.Error);
        FileSystemMutationResult moved = await system.MoveAsync(
                new FileSystemMoveRequest(
                    source,
                    destination,
                    entry.Value!.Metadata.Revision,
                    sourceParent.Value!.Metadata.Revision,
                    destinationParent.Value!.Metadata.Revision),
                cancellationToken)
            .ConfigureAwait(false);
        Ensure(moved.Succeeded ? null : moved.Transaction.Error);
    }
}
