using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Processes;
using HackerOs.Platform.Core.Sounds;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Sounds;

namespace HackerOs.Platform.Core.FileSystem;

/// <summary>Creates the required Linux-like clean-profile directory layout exactly once.</summary>
/// <param name="fileSystem">Mounted filesystem used for the ordinary layout.</param>
/// <param name="timeProvider">Optional time source for authorization contexts.</param>
/// <param name="mountRouter">
/// Optional mount router. When supplied, the empty mount-point directories <c>/proc</c> and
/// <c>/dev</c> are created directly in the root provider (ADR 0046): the router does not merge
/// mounts into their parent's listing, so without a real directory underneath, <c>ls /</c> would
/// not show them. Going through <paramref name="fileSystem"/> would reach the virtual provider
/// that shadows the path instead.
/// </param>
public sealed class FileSystemSeeder(
    IFileSystemService fileSystem,
    TimeProvider? timeProvider = null,
    IFileSystemMountRouter? mountRouter = null)
{
    /// <summary>
    /// Gets the app sources and block list the host was built with (ADR 0189 §1, §5), written to
    /// <c>/etc/hackeros/app-sources.config</c> and <c>app-policy.config</c> when they do not exist.
    /// </summary>
    public Apps.Packages.AppPackageDefaults PackageDefaults { get; init; } = Apps.Packages.AppPackageDefaults.Standard;

    private readonly IFileSystemService _fileSystem =
        fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IFileSystemMountRouter? _mountRouter = mountRouter;

    // A seed directory whose parent keeps changing under it (concurrent boot writers) is retried
    // this many times before the boot fails.
    private const int MaxSeedAttempts = 3;

    /// <summary>Ensures the system layout and one user's standard directories exist.</summary>
    public async ValueTask SeedAsync(
        string userId,
        string primaryGroupId,
        CancellationToken cancellationToken = default)
    {
        FileSystemEntryName userName = FileSystemEntryName.Parse(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryGroupId);

        FileSystemAuthorizationContext system = CreateContext("system", "system");
        FileSystemAuthorizationContext userProvisioning = CreateContext(userName.Value, primaryGroupId);

        await EnsureDirectoryAsync("/bin", 0x01ED, system, cancellationToken);
        await EnsureDirectoryAsync("/etc", 0x01ED, system, cancellationToken);
        await EnsureDirectoryAsync("/home", 0x01ED, system, cancellationToken);
        await EnsureDirectoryAsync("/tmp", 0x01FF, system, cancellationToken);
        await EnsureDirectoryAsync("/var", 0x01ED, system, cancellationToken);
        await EnsureDirectoryAsync("/var/log", 0x01ED, system, cancellationToken);
        await EnsureDirectoryAsync("/var/log/apps", 0x01ED, system, cancellationToken);
        await EnsureMountPointAsync("/proc", 0x016D, system, cancellationToken);
        await EnsureMountPointAsync("/dev", 0x01ED, system, cancellationToken);
        // ADR 0181: network drives (folders of the host computer) are mounted in /mnt/<name>.
        await EnsureMountPointAsync("/mnt", 0x01ED, system, cancellationToken);

        // ADR 0044: OS settings (system layer) and OS state such as migration markers.
        await EnsureDirectoryAsync("/etc/hackeros", 0x01ED, system, cancellationToken);
        await EnsureDirectoryAsync("/var/lib", 0x01ED, system, cancellationToken);
        await EnsureDirectoryAsync("/var/lib/hackeros", 0x01ED, system, cancellationToken);

        // ADR 0050: kernel host name and pam_env-style system environment, root-owned 0644.
        await EnsureFileAsync(
            LoginEnvironment.HostNameFilePath.Value, LoginEnvironment.DefaultHostName + "\n", 0x01A4, system, cancellationToken);
        await EnsureFileAsync(
            LoginEnvironment.EnvironmentFilePath.Value, LoginEnvironment.DefaultEnvironmentFileContent, 0x01A4, system, cancellationToken);
        // ADR 0104: systemd-style system locale, root-owned 0644, changed with localectl.
        await EnsureFileAsync(
            LoginEnvironment.SystemLocaleFilePath.Value, LoginEnvironment.DefaultSystemLocaleFileContent, 0x01A4, system, cancellationToken);
        // ADR 0074, section 4: the static host table read first by hackdns, root-owned 0644.
        await EnsureFileAsync(
            Network.Local.HackDnsResolver.HostsFilePath.Value, Network.Local.HackDnsResolver.DefaultHostsFileContent, 0x01A4, system, cancellationToken);
        // ADR 0182 section 3, ADR 0189 section 1: hosts, catalogs and trusted sources of app packages,
        // from the host's build, root-owned 0644.
        await EnsureFileAsync(
            Apps.Packages.AppSourcesConfigDocument.MachinePath.Value,
            Apps.Packages.AppSourcesConfigDocument.BuildDefaultContent(PackageDefaults),
            0x01A4,
            system,
            cancellationToken);
        // ADR 0189 section 5: the administrator's block list for personal installs, root-owned 0644.
        await EnsureFileAsync(
            Apps.Packages.AppPolicyConfigDocument.MachinePath.Value,
            Apps.Packages.AppPolicyConfigDocument.BuildDefaultContent(PackageDefaults),
            0x01A4,
            system,
            cancellationToken);

        // ADR 0125, section 3: pam_limits-style session limits, every line commented, root-owned 0644.
        await EnsureDirectoryAsync("/etc/security", 0x01ED, system, cancellationToken);
        await EnsureDirectoryAsync(ResourceLimitsConfiguration.LimitsDirectoryPath.Value, 0x01ED, system, cancellationToken);
        await EnsureFileAsync(
            ResourceLimitsConfiguration.LimitsFilePath.Value, ResourceLimitsConfiguration.DefaultLimitsFileContent, 0x01A4, system, cancellationToken);

        // ADR 0077/0080: declarations of the hosted static sites (hackwebd), edited by an administrator.
        await EnsureDirectoryAsync("/etc/hackweb", 0x01ED, system, cancellationToken);
        await EnsureDirectoryAsync("/etc/hackweb/sites", 0x01ED, system, cancellationToken);

        // The shared data in /usr/share (sound themes, icon packs) is installed after the sign-in,
        // in the background: InstallSoundThemesAsync and InstallIconPacksAsync.
        await EnsureDirectoryAsync("/usr", 0x01ED, system, cancellationToken);
        await EnsureDirectoryAsync("/usr/share", 0x01ED, system, cancellationToken);

        string home = $"/home/{userName.Value}";
        await EnsureDirectoryAsync(home, 0x01C0, userProvisioning, cancellationToken);
        await EnsureDirectoryAsync($"{home}/Desktop", 0x01C0, userProvisioning, cancellationToken);
        await EnsureDirectoryAsync($"{home}/Documents", 0x01C0, userProvisioning, cancellationToken);
        await EnsureDirectoryAsync($"{home}/Downloads", 0x01C0, userProvisioning, cancellationToken);
        await EnsureDirectoryAsync($"{home}/.config", 0x01C0, userProvisioning, cancellationToken);
        // ADR 0044: settings directories are 0755 like on Linux; the 0700 home already keeps
        // other users out. ~/.config/<appId>/ is created by HackSettings on first write.
        await EnsureDirectoryAsync($"{home}/.config/hackeros", 0x01ED, userProvisioning, cancellationToken);
        await EnsureDirectoryAsync($"{home}/.local", 0x01ED, userProvisioning, cancellationToken);
        await EnsureDirectoryAsync($"{home}/.local/state", 0x01ED, userProvisioning, cancellationToken);
        await EnsureDirectoryAsync($"{home}/.local/state/hackeros", 0x01ED, userProvisioning, cancellationToken);
        // ADR 0204: the user's own sound themes, one folder per theme.
        await EnsureDirectoryAsync($"{home}/.local/share", 0x01ED, userProvisioning, cancellationToken);
        await EnsureDirectoryAsync(
            OsSoundEvents.GetUserThemesDirectory(home), 0x01ED, userProvisioning, cancellationToken);
    }

    /// <summary>
    /// Installs the icon packs shipped with the OS (ADR 0203 §6) in <c>/usr/share/icons</c>, root-owned
    /// 0644 like on Linux. Existing files are kept, so an administrator's edits survive.
    /// </summary>
    /// <remarks>
    /// Kept out of <see cref="SeedAsync"/> because writing the pack's images takes about ten seconds
    /// in the browser store at the first login: like a distribution's package post-install step, it
    /// runs in the background once the session is open (<see cref="Sessions.LocalSessionService"/>).
    /// Nothing in the session waits for it: the built-in icons are the default pack.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask InstallIconPacksAsync(CancellationToken cancellationToken = default)
    {
        FileSystemAuthorizationContext system = CreateContext("system", "system");
        string list = InstalledList(BuiltInIconPacks.Files.Select(file =>
            ($"{Theming.Abstractions.IconPacks.IconPackLibrary.SystemDirectory}/{file.RelativePath}", file.Content.LongLength)));
        if (await IsInstalledAsync(IconPacksListName, list, system, cancellationToken))
        {
            return;
        }

        await EnsureDirectoryAsync("/usr", 0x01ED, system, cancellationToken);
        await EnsureDirectoryAsync("/usr/share", 0x01ED, system, cancellationToken);
        await EnsureDirectoryAsync(Theming.Abstractions.IconPacks.IconPackLibrary.SystemDirectory, 0x01ED, system, cancellationToken);
        foreach (BuiltInIconPacks.PackFile file in BuiltInIconPacks.Files)
        {
            string target = $"{Theming.Abstractions.IconPacks.IconPackLibrary.SystemDirectory}/{file.RelativePath}";
            string folder = Theming.Abstractions.IconPacks.IconPackLibrary.SystemDirectory;
            foreach (string part in file.RelativePath.Split('/')[..^1])
            {
                folder += "/" + part;
                await EnsureDirectoryAsync(folder, 0x01ED, system, cancellationToken);
            }

            if (file.RelativePath.EndsWith(".json", StringComparison.Ordinal))
            {
                // icon-pack.json stays a text file, readable with cat and editable with nano.
                await EnsureFileAsync(target, System.Text.Encoding.UTF8.GetString(file.Content), 0x01A4, system, cancellationToken, completeEmpty: true);
            }
            else
            {
                await EnsureFileAsync(target, file.Content, 0x01A4, system, cancellationToken, completeEmpty: true);
            }
        }

        await RecordInstalledAsync(IconPacksListName, list, system, cancellationToken);
    }

    /// <summary>
    /// Installs every built-in sound theme (ADR 0204 section 4): <c>index.theme</c> and one
    /// <c>stereo/&lt;sound-name&gt;.oga</c> per event, root-owned 0644. Existing files are kept.
    /// </summary>
    /// <remarks>
    /// Not part of <see cref="SeedAsync"/>: the sound service calls it after the first sign-in, in
    /// the background, so the login does not wait for about fifty kilobytes of audio.
    /// </remarks>
    /// <param name="cancellationToken">Signals cancellation of the writes.</param>
    public async ValueTask InstallSoundThemesAsync(CancellationToken cancellationToken = default)
    {
        FileSystemAuthorizationContext system = CreateContext("system", "system");
        string list = InstalledList(SoundThemeFiles());
        if (await IsInstalledAsync(SoundThemesListName, list, system, cancellationToken))
        {
            return;
        }

        await EnsureDirectoryAsync("/usr", 0x01ED, system, cancellationToken);
        await EnsureDirectoryAsync("/usr/share", 0x01ED, system, cancellationToken);
        await EnsureDirectoryAsync(OsSoundEvents.SystemThemesDirectory, 0x01ED, system, cancellationToken);
        foreach (BuiltInSoundTheme theme in BuiltInSoundThemes.All)
        {
            string directory = BuiltInSoundThemes.GetDirectory(theme.Id);
            await EnsureDirectoryAsync(directory, 0x01ED, system, cancellationToken);
            await EnsureDirectoryAsync(directory + "/stereo", 0x01ED, system, cancellationToken);
            await EnsureFileAsync(
                $"{directory}/{OsSoundEvents.IndexFileName}", BuiltInSoundThemes.GetIndexContent(theme), 0x01A4, system, cancellationToken, completeEmpty: true);
            foreach (OsSoundEvent soundEvent in OsSoundEvents.All)
            {
                if (BuiltInSoundThemes.ReadSound(theme.Id, soundEvent) is { } audio)
                {
                    await EnsureFileAsync(
                        BuiltInSoundThemes.GetSoundPath(theme.Id, soundEvent),
                        audio,
                        0x01A4,
                        system,
                        cancellationToken,
                        completeEmpty: true);
                }
            }
        }

        await RecordInstalledAsync(SoundThemesListName, list, system, cancellationToken);
    }

    /// <summary>
    /// Ensures an empty mount-point directory exists in the root provider, like the directories a
    /// Linux distribution ships so <c>/proc</c> and <c>/dev</c> have somewhere to be mounted.
    /// </summary>
    private ValueTask EnsureMountPointAsync(
        string pathValue,
        ushort mode,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken)
    {
        IFileSystemProvider target = _mountRouter?.Resolve(VirtualPath.Parse("/")).Mount.Provider ?? _fileSystem;
        return EnsureDirectoryAsync(pathValue, mode, context, cancellationToken, target);
    }

    private async ValueTask EnsureDirectoryAsync(
        string pathValue,
        ushort mode,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken,
        IFileSystemProvider? provider = null,
        int attempt = 0)
    {
        IFileSystemProvider fileSystem = provider ?? _fileSystem;
        VirtualPath path = VirtualPath.Parse(pathValue);
        FileSystemResult<FileSystemEntrySnapshot> existing = await fileSystem.StatAsync(
            new FileSystemStatRequest(path, FileSystemLinkBehavior.NoFollow),
            context,
            cancellationToken);
        if (existing.Succeeded)
        {
            if (existing.Value!.Metadata.Kind != FileSystemEntryKind.Directory)
            {
                throw new InvalidOperationException($"Seed path '{path}' exists but is not a directory.");
            }

            return;
        }

        if (existing.Error?.Code != FileSystemErrorCode.NotFound)
        {
            throw new InvalidOperationException(
                $"Cannot inspect seed path '{path}': {existing.Error?.Code}.");
        }

        VirtualPath parent = GetParent(path);
        FileSystemResult<FileSystemEntrySnapshot> parentResult = await fileSystem.StatAsync(
            new FileSystemStatRequest(parent),
            context,
            cancellationToken);
        if (!parentResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"Cannot inspect seed parent '{parent}': {parentResult.Error?.Code}.");
        }

        FileSystemMutationResult created = await fileSystem.CreateAsync(
            new FileSystemCreateRequest(
                path,
                FileSystemEntryKind.Directory,
                FileSystemPermissions.FromMode(mode),
                parentResult.Value!.Metadata.Revision),
            context,
            cancellationToken);
        if (!created.Succeeded
            && created.Transaction.Error?.Code == FileSystemErrorCode.RevisionConflict
            && attempt < MaxSeedAttempts)
        {
            // The parent changed between the stat and the create: boot services (syslog, app
            // logs) write into /var/log while the seeder runs. Re-read the parent and retry, like
            // `mkdir -p` racing another process.
            await EnsureDirectoryAsync(pathValue, mode, context, cancellationToken, provider, attempt + 1);
            return;
        }

        if (!created.Succeeded && created.Transaction.Error?.Code != FileSystemErrorCode.AlreadyExists)
        {
            throw new InvalidOperationException(
                $"Cannot create seed path '{path}': {created.Transaction.Error?.Code}.");
        }
    }

    /// <summary>
    /// Creates a file with <paramref name="content"/> when nothing exists at <paramref name="pathValue"/>.
    /// An existing file is never touched: an administrator's edits survive every login.
    /// </summary>
    private ValueTask EnsureFileAsync(
        string pathValue,
        string content,
        ushort mode,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken,
        bool completeEmpty = false) =>
        EnsureFileAsync(pathValue, new SeedSource(System.Text.Encoding.UTF8.GetBytes(content), FileSystemContentDescriptor.Text()), mode, context, cancellationToken, completeEmpty);

    /// <summary>Creates a binary file (an image of a shipped icon pack, a built-in sound) when nothing exists at <paramref name="pathValue"/>.</summary>
    private ValueTask EnsureFileAsync(
        string pathValue,
        byte[] content,
        ushort mode,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken,
        bool completeEmpty = false) =>
        EnsureFileAsync(pathValue, new SeedSource(content, FileSystemContentDescriptor.Binary(MediaTypeOf(pathValue))), mode, context, cancellationToken, completeEmpty);

    // completeEmpty also writes an existing empty file: a background install interrupted by a
    // reload between creating a file and writing it leaves it empty, and the next boot completes it.
    private async ValueTask EnsureFileAsync(
        string pathValue,
        SeedSource content,
        ushort mode,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken,
        bool completeEmpty = false)
    {
        VirtualPath path = VirtualPath.Parse(pathValue);
        FileSystemResult<FileSystemEntrySnapshot> existing = await _fileSystem.StatAsync(
            new FileSystemStatRequest(path, FileSystemLinkBehavior.NoFollow),
            context,
            cancellationToken);
        bool interrupted = completeEmpty
            && existing.Value?.Metadata is FileMetadata { Length: 0 }
            && content.Length > 0;
        if (existing.Succeeded && !interrupted)
        {
            return;
        }

        if (!interrupted)
        {
            if (existing.Error?.Code != FileSystemErrorCode.NotFound)
            {
                throw new InvalidOperationException($"Cannot inspect seed file '{path}': {existing.Error?.Code}.");
            }

            FileSystemMutationResult created = await _fileSystem.CreateAsync(
                new FileSystemCreateRequest(path, FileSystemEntryKind.File, FileSystemPermissions.FromMode(mode)),
                context,
                cancellationToken);
            if (!created.Succeeded)
            {
                if (created.Transaction.Error?.Code == FileSystemErrorCode.AlreadyExists)
                {
                    return;
                }

                throw new InvalidOperationException($"Cannot create seed file '{path}': {created.Transaction.Error?.Code}.");
            }
        }

        FileSystemMutationResult written = await _fileSystem.WriteAsync(
            new FileSystemWriteRequest(path),
            content,
            context,
            cancellationToken);
        if (!written.Succeeded)
        {
            throw new InvalidOperationException($"Cannot write seed file '{path}': {written.Transaction.Error?.Code}.");
        }
    }

    // dpkg keeps the files of each installed package in /var/lib/dpkg/info/<package>.list; the
    // background installs keep theirs here, so a boot whose list matches the shipped files skips
    // the install instead of checking every file again in the browser store.
    private const string InstalledListsDirectory = "/var/lib/hackeros/installed";
    private const string IconPacksListName = "icon-packs.list";
    private const string SoundThemesListName = "sound-themes.list";

    private static VirtualPath GetInstalledListPath(string name) => VirtualPath.Parse($"{InstalledListsDirectory}/{name}");

    /// <summary>One line per shipped file, "path length", in the order of the install.</summary>
    private static string InstalledList(IEnumerable<(string Path, long Length)> files) =>
        string.Concat(files.Select(file => $"{file.Path} {file.Length}\n"));

    private static IEnumerable<(string Path, long Length)> SoundThemeFiles()
    {
        foreach (BuiltInSoundTheme theme in BuiltInSoundThemes.All)
        {
            yield return (
                $"{BuiltInSoundThemes.GetDirectory(theme.Id)}/{OsSoundEvents.IndexFileName}",
                System.Text.Encoding.UTF8.GetByteCount(BuiltInSoundThemes.GetIndexContent(theme)));
            foreach (OsSoundEvent soundEvent in OsSoundEvents.All)
            {
                if (BuiltInSoundThemes.ReadSound(theme.Id, soundEvent) is { } audio)
                {
                    yield return (BuiltInSoundThemes.GetSoundPath(theme.Id, soundEvent), audio.LongLength);
                }
            }
        }
    }

    /// <summary>Whether the install's list already records exactly these files.</summary>
    private async ValueTask<bool> IsInstalledAsync(
        string name, string list, FileSystemAuthorizationContext context, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemContentReadHandle> read = await _fileSystem.ReadAsync(
            new FileSystemReadRequest(GetInstalledListPath(name)), context, cancellationToken);
        if (!read.Succeeded || read.Value is null)
        {
            return false;
        }

        await using FileSystemContentReadHandle handle = read.Value;
        using StreamReader reader = new(handle.Content, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return string.Equals(await reader.ReadToEndAsync(cancellationToken), list, StringComparison.Ordinal);
    }

    /// <summary>Writes the install's list once every file is in place, root-owned 0644.</summary>
    private async ValueTask RecordInstalledAsync(
        string name, string list, FileSystemAuthorizationContext context, CancellationToken cancellationToken)
    {
        await EnsureDirectoryAsync("/var/lib", 0x01ED, context, cancellationToken);
        await EnsureDirectoryAsync("/var/lib/hackeros", 0x01ED, context, cancellationToken);
        await EnsureDirectoryAsync(InstalledListsDirectory, 0x01ED, context, cancellationToken);
        VirtualPath path = GetInstalledListPath(name);
        FileSystemMutationResult created = await _fileSystem.CreateAsync(
            new FileSystemCreateRequest(path, FileSystemEntryKind.File, FileSystemPermissions.FromMode(0x01A4)),
            context,
            cancellationToken);
        if (!created.Succeeded && created.Transaction.Error?.Code != FileSystemErrorCode.AlreadyExists)
        {
            throw new InvalidOperationException($"Cannot create install list '{path}': {created.Transaction.Error?.Code}.");
        }

        FileSystemMutationResult written = await _fileSystem.WriteAsync(
            new FileSystemWriteRequest(path),
            new SeedSource(System.Text.Encoding.UTF8.GetBytes(list), FileSystemContentDescriptor.Text()),
            context,
            cancellationToken);
        if (!written.Succeeded)
        {
            throw new InvalidOperationException($"Cannot write install list '{path}': {written.Transaction.Error?.Code}.");
        }
    }

    private FileSystemAuthorizationContext CreateContext(string userId, string groupId)
    {
        AppOperationContext operation = new()
        {
            AppId = "org.hackeros.kernel",
            UserId = userId,
            UserAuthority = AppAuthority.System,
            GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
            IsSystemOperation = true
        };
        return new FileSystemAuthorizationContext(
            operation,
            [groupId],
            _timeProvider.GetUtcNow().ToUniversalTime());
    }

    private static string MediaTypeOf(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
        : path.EndsWith(".oga", StringComparison.OrdinalIgnoreCase) ? "audio/ogg"
        : "application/octet-stream";

    /// <summary>Content of one seeded file: UTF-8 text or the bytes of an image.</summary>
    private sealed class SeedSource(byte[] bytes, FileSystemContentDescriptor descriptor) : IFileSystemContentSource
    {
        private readonly byte[] _bytes = bytes;

        public FileSystemContentDescriptor Descriptor { get; } = descriptor;

        public long? Length => _bytes.LongLength;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(_bytes, writable: false));
        }
    }

    private static VirtualPath GetParent(VirtualPath path)
    {
        int separator = path.Value.LastIndexOf('/');
        return separator <= 0 ? VirtualPath.Parse("/") : VirtualPath.Parse(path.Value[..separator]);
    }
}