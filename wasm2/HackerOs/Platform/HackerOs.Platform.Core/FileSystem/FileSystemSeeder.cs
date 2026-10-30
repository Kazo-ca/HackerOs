using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Processes;
using HackerOs.Simulation.Abstractions.FileSystem;

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
    private async ValueTask EnsureFileAsync(
        string pathValue,
        string content,
        ushort mode,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken)
    {
        VirtualPath path = VirtualPath.Parse(pathValue);
        FileSystemResult<FileSystemEntrySnapshot> existing = await _fileSystem.StatAsync(
            new FileSystemStatRequest(path, FileSystemLinkBehavior.NoFollow),
            context,
            cancellationToken);
        if (existing.Succeeded)
        {
            return;
        }

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

        FileSystemMutationResult written = await _fileSystem.WriteAsync(
            new FileSystemWriteRequest(path),
            new SeedTextSource(content),
            context,
            cancellationToken);
        if (!written.Succeeded)
        {
            throw new InvalidOperationException($"Cannot write seed file '{path}': {written.Transaction.Error?.Code}.");
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

    /// <summary>UTF-8 text content of one seeded file.</summary>
    private sealed class SeedTextSource(string content) : IFileSystemContentSource
    {
        private readonly byte[] _bytes = System.Text.Encoding.UTF8.GetBytes(content);

        public FileSystemContentDescriptor Descriptor { get; } = FileSystemContentDescriptor.Text();

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