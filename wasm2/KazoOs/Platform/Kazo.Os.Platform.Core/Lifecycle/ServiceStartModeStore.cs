using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.Lifecycle;

/// <summary>
/// Reads and writes the per-user, per-service start-mode config file at
/// <c>/home/{userName}/.config/{appId}/service.conf</c> -- or, for a system service started at boot
/// (ADR 0097, section 4), the machine's <c>/etc/{appId}/service.conf</c> -- the live, Linux-rc-style
/// enabled/disabled state that lives alongside, but independently of, the manifest's
/// <see cref="AppManifest.AutoStart"/> preset. Every call here is trusted, system-authority IO:
/// callers are expected to have already authorized the request one layer up (same-assembly or
/// <see cref="AppCapabilities.ServicesManage"/>), the same way
/// <c>AppLifecycleOrchestrator.InvalidateAssociationDefaultsAsync</c> hand-builds a system context
/// to touch settings it doesn't own.
/// </summary>
internal static class ServiceStartModeStore
{
    private const string StartModeKey = "StartMode=";

    /// <summary>Reads the effective start mode, or <paramref name="fallback"/> when no config file exists yet.</summary>
    public static ValueTask<ServiceStartMode> ReadAsync(
        IFileSystemService fileSystem,
        string userName,
        string appId,
        ServiceStartMode fallback,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return ReadCoreAsync(fileSystem, UserLocation(userName, appId), fallback, cancellationToken);
    }

    /// <summary>
    /// Reads the effective start mode of a system service from <c>/etc/{appId}/service.conf</c>
    /// (ADR 0097, section 4), or <paramref name="fallback"/> when the file does not exist yet.
    /// </summary>
    public static ValueTask<ServiceStartMode> ReadSystemAsync(
        IFileSystemService fileSystem,
        string appId,
        ServiceStartMode fallback,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return ReadCoreAsync(fileSystem, SystemLocation(appId), fallback, cancellationToken);
    }

    /// <summary>Writes the effective start mode, creating the app's config directory/file if needed.</summary>
    public static ValueTask WriteAsync(
        IFileSystemService fileSystem,
        string userName,
        string appId,
        ServiceStartMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return WriteCoreAsync(fileSystem, UserLocation(userName, appId), appId, mode, cancellationToken);
    }

    /// <summary>
    /// Writes the effective start mode of a system service to <c>/etc/{appId}/service.conf</c>
    /// (<c>root:root 0644</c>). The caller checked that an administrator asked.
    /// </summary>
    public static ValueTask WriteSystemAsync(
        IFileSystemService fileSystem,
        string appId,
        ServiceStartMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return WriteCoreAsync(fileSystem, SystemLocation(appId), appId, mode, cancellationToken);
    }

    private static async ValueTask<ServiceStartMode> ReadCoreAsync(
        IFileSystemService fileSystem,
        Location location,
        ServiceStartMode fallback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        FileSystemResult<FileSystemContentReadHandle> result = await fileSystem.ReadAsync(
            new FileSystemReadRequest(location.File), location.Context, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Value is null)
        {
            return fallback;
        }

        await using FileSystemContentReadHandle handle = result.Value;
        using StreamReader reader = new(handle.Content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return Parse(content, fallback);
    }

    private static async ValueTask WriteCoreAsync(
        IFileSystemService fileSystem,
        Location location,
        string appId,
        ServiceStartMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        FileSystemMutationResult createDirectory = await fileSystem.CreateAsync(
            new FileSystemCreateRequest(location.Directory, FileSystemEntryKind.Directory, FileSystemPermissions.FromMode(location.DirectoryMode)),
            location.Context,
            cancellationToken).ConfigureAwait(false);
        if (!createDirectory.Succeeded && createDirectory.Transaction.Error?.Code != FileSystemErrorCode.AlreadyExists)
        {
            throw new InvalidOperationException(
                $"Could not create service config directory for '{appId}': {createDirectory.Transaction.Error?.Code}.");
        }

        FileSystemMutationResult createFile = await fileSystem.CreateAsync(
            new FileSystemCreateRequest(location.File, FileSystemEntryKind.File, FileSystemPermissions.FromMode(location.FileMode)),
            location.Context,
            cancellationToken).ConfigureAwait(false);
        if (!createFile.Succeeded && createFile.Transaction.Error?.Code != FileSystemErrorCode.AlreadyExists)
        {
            throw new InvalidOperationException(
                $"Could not create service config file for '{appId}': {createFile.Transaction.Error?.Code}.");
        }

        FileSystemMutationResult write = await fileSystem.WriteAsync(
            new FileSystemWriteRequest(location.File), new TextContentSource($"{StartModeKey}{mode}\n"), location.Context, cancellationToken)
            .ConfigureAwait(false);
        if (!write.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not write service config file for '{appId}': {write.Transaction.Error?.Code}.");
        }
    }

    /// <summary>Gets the path of a system service's start-mode file (ADR 0097, section 4).</summary>
    internal static VirtualPath GetSystemConfigPath(string appId) => VirtualPath.Parse($"/etc/{appId}/service.conf");

    private static Location UserLocation(string userName, string appId) => new(
        VirtualPath.Parse($"/home/{userName}/.config/{appId}"),
        VirtualPath.Parse($"/home/{userName}/.config/{appId}/service.conf"),
        BuildContext(userName, appId),
        0b111_000_000,
        0b110_000_000);

    private static Location SystemLocation(string appId) => new(
        VirtualPath.Parse($"/etc/{appId}"),
        GetSystemConfigPath(appId),
        // Like /etc/hackeros/apps.config: the OS writes it as root, everyone may read it.
        new FileSystemAuthorizationContext(Settings.OsSettingsService.CreateSystemOperation(), ["system"], DateTimeOffset.UtcNow),
        0b111_101_101,
        0b110_100_100);

    private static FileSystemAuthorizationContext BuildContext(string userName, string appId)
    {
        // A path under /home/{user}/.config/{appId} resolves to the app-private capability
        // pair (FileSystemService.ResolveCapability), not the broader user-home one -- this is the
        // service's own private config, not general home access. UserName must be the real login
        // name (not a literal "system") so that path-prefix match succeeds.
        AppOperationContext operationContext = new()
        {
            AppId = appId,
            UserId = userName,
            UserName = userName,
            UserAuthority = AppAuthority.System,
            GrantedCapabilities = new HashSet<string>(StringComparer.Ordinal)
            {
                AppCapabilities.FileSystemPrivateRead,
                AppCapabilities.FileSystemPrivateWrite
            },
            IsSystemOperation = true
        };

        return new FileSystemAuthorizationContext(operationContext, groupIds: [], DateTimeOffset.UtcNow);
    }

    private static ServiceStartMode Parse(string content, ServiceStartMode fallback)
    {
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith(StartModeKey, StringComparison.Ordinal))
            {
                continue;
            }

            string value = line[StartModeKey.Length..].Trim();
            if (Enum.TryParse(value, ignoreCase: false, out ServiceStartMode mode) && Enum.IsDefined(mode))
            {
                return mode;
            }
        }

        return fallback;
    }

    /// <summary>Where one start-mode file lives and who writes it.</summary>
    private sealed record Location(
        VirtualPath Directory, VirtualPath File, FileSystemAuthorizationContext Context, ushort DirectoryMode, ushort FileMode);

    private sealed class TextContentSource(string content) : IFileSystemContentSource
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(content);

        public FileSystemContentDescriptor Descriptor { get; } = FileSystemContentDescriptor.Text();

        public long? Length => _bytes.LongLength;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(_bytes, writable: false));
        }
    }
}
