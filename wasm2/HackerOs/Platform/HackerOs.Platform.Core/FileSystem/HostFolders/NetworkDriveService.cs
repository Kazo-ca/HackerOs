using System.Text.Json;
using System.Text.Json.Serialization;
using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HackerOs.Platform.Core.FileSystem.HostFolders;

/// <summary>One network drive of the signed-in user (ADR 0181).</summary>
/// <param name="MountPoint">Where it is mounted, e.g. <c>/mnt/Projects</c>.</param>
/// <param name="Name">Name of the folder on the host computer.</param>
/// <param name="OwnerId">Login name of the user who mounted it.</param>
/// <param name="IsConnected">Whether the browser's permission is granted.</param>
public sealed record NetworkDrive(VirtualPath MountPoint, string Name, string OwnerId, bool IsConnected);

/// <summary>Why a network drive operation was refused.</summary>
public enum NetworkDriveError
{
    /// <summary>The browser has no folder dialog (Firefox, Safari, mobile).</summary>
    NotSupported,

    /// <summary>Nobody is signed in.</summary>
    NotSignedIn,

    /// <summary>The mount point is not <c>/mnt/&lt;name&gt;</c>, or is not an empty folder of the user.</summary>
    InvalidMountPoint,

    /// <summary>No network drive is mounted there.</summary>
    NotMounted,

    /// <summary>The drive belongs to another user and the caller is not an administrator.</summary>
    NotOwner,

    /// <summary>The browser asks for a click or a key first.</summary>
    UserActivationRequired,

    /// <summary>The user refused the browser's permission.</summary>
    PermissionDenied
}

/// <summary>A refused network drive operation.</summary>
public sealed class NetworkDriveException(NetworkDriveError error, string message) : InvalidOperationException(message)
{
    /// <summary>Gets why the operation was refused.</summary>
    public NetworkDriveError Error { get; } = error;
}

/// <summary>
/// Mounts folders of the host computer as network drives (ADR 0181, sections 4 to 8): opens the
/// browser's folder dialog, mounts a <see cref="HostFolderFileSystemProvider"/> at
/// <c>/mnt/&lt;name&gt;</c>, and keeps the user's drives in
/// <c>~/.local/state/hackeros/network-drives.json</c> (device-local, never synchronized, since the
/// browser's handles stay in this browser).
/// </summary>
/// <remarks>
/// At login the user's drives are mounted again, disconnected when the browser asks for its
/// permission again; at logout they are unmounted. Mount-point folders and the drive list are
/// written straight to the root provider, as the kernel, like <see cref="FileSystemSeeder"/> does.
/// </remarks>
public sealed class NetworkDriveService : IDisposable
{
    /// <summary>Folder of the mount points.</summary>
    public static readonly VirtualPath MountRoot = VirtualPath.Parse("/mnt");

    /// <summary>The drive list, relative to the home folder.</summary>
    public const string TableRelativePath = ".local/state/hackeros/network-drives.json";

    private const ushort MountPointMode = 0x1C0; // 0700
    private const ushort TableMode = 0x180; // 0600

    private readonly IDynamicFileSystemMountRouter _router;
    private readonly IHostFolderAccess _access;
    private readonly Func<AuthenticatedPrincipal?> _principal;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IDisposable[] _subscriptions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, MountedDrive> _mounted = new(StringComparer.Ordinal);

    /// <summary>Initializes the service.</summary>
    /// <param name="router">The mount table.</param>
    /// <param name="access">The browser's folder dialog and handle store.</param>
    /// <param name="principal">The signed-in user, resolved lazily (the session service needs the file system).</param>
    /// <param name="eventBus">Login and logout events; <see langword="null"/> in tests that call <see cref="RestoreAsync"/> themselves.</param>
    /// <param name="timeProvider">Clock of the authorization contexts.</param>
    /// <param name="logger">Logs drives that cannot be mounted again.</param>
    public NetworkDriveService(
        IDynamicFileSystemMountRouter router,
        IHostFolderAccess access,
        Func<AuthenticatedPrincipal?> principal,
        IEventBus? eventBus = null,
        TimeProvider? timeProvider = null,
        ILogger<NetworkDriveService>? logger = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _principal = principal ?? throw new ArgumentNullException(nameof(principal));
        _time = timeProvider ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _subscriptions = eventBus is null
            ? []
            :
            [
                eventBus.Subscribe<SessionActivatedEvent>(_ => Observe(RestoreAsync().AsTask(), "restore")),
                eventBus.Subscribe<SessionLoggedOutEvent>(_ => Observe(UnmountAllAsync().AsTask(), "unmount"))
            ];
    }

    /// <summary>Raised after a drive is mounted, unmounted, connected or disconnected.</summary>
    public event Action? Changed;

    /// <summary>Gets whether the browser can mount a host folder.</summary>
    public ValueTask<bool> IsSupportedAsync(CancellationToken cancellationToken = default) =>
        _access.IsSupportedAsync(cancellationToken);

    /// <summary>
    /// Gets the signed-in user's drives (every drive for an administrator), by mount point; none
    /// when nobody is signed in.
    /// </summary>
    public IReadOnlyList<NetworkDrive> GetDrives()
    {
        if (_principal() is not { } principal)
        {
            return [];
        }

        lock (_mounted)
        {
            return
            [
                .. _mounted.Values
                    .Where(drive => Sees(principal, drive))
                    .OrderBy(static drive => drive.Provider.MountPath.Value, StringComparer.Ordinal)
                    .Select(static drive => drive.Describe())
            ];
        }
    }

    /// <summary>Gets the drive mounted at <paramref name="mountPoint"/>, if any.</summary>
    public NetworkDrive? Find(VirtualPath mountPoint)
    {
        lock (_mounted)
        {
            return _mounted.TryGetValue(mountPoint.Value, out MountedDrive? drive) ? drive.Describe() : null;
        }
    }

    /// <summary>
    /// Opens the browser's folder dialog and mounts the chosen folder for the signed-in user;
    /// <see langword="null"/> when the user closed the dialog. Needs a recent user gesture.
    /// </summary>
    /// <param name="mountPoint"><c>/mnt/&lt;name&gt;</c>, or <see langword="null"/> for <c>/mnt/&lt;folder name&gt;</c>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="NetworkDriveException">The browser cannot, nobody is signed in, or the mount point is not usable.</exception>
    public async ValueTask<NetworkDrive?> MountAsync(string? mountPoint, CancellationToken cancellationToken = default)
    {
        AuthenticatedPrincipal principal = RequirePrincipal();
        VirtualPath? requested = mountPoint is null ? null : ParseMountPoint(mountPoint);
        if (!await _access.IsSupportedAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new NetworkDriveException(NetworkDriveError.NotSupported, "hostfs: not supported by this browser");
        }

        HostFolderPick? pick;
        try
        {
            pick = await _access.PickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (exception is not NetworkDriveException)
        {
            throw new NetworkDriveException(NetworkDriveError.UserActivationRequired, exception.Message);
        }

        if (pick is null)
        {
            return null;
        }

        MountedDrive? mounted = null;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _access.ForgetAsync(pick.Key, CancellationToken.None).ConfigureAwait(false);
            await pick.Folder.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        try
        {
            IFileSystemProvider root = RootProvider;
            FileSystemAuthorizationContext context = Context(principal);
            await EnsureMountRootAsync(root, cancellationToken).ConfigureAwait(false);
            (VirtualPath path, bool created) = requested is { } chosen
                ? (chosen, await PrepareMountPointAsync(root, chosen, principal, context, cancellationToken).ConfigureAwait(false))
                : (await CreateFreeMountPointAsync(root, pick.Folder.Name, context, cancellationToken).ConfigureAwait(false), true);
            DriveRecord record = new(path.Value, pick.Folder.Name, pick.Key, created);
            mounted = Mount(record, pick.Folder, principal, connected: true);
            await SaveTableAsync(principal, cancellationToken).ConfigureAwait(false);
            return mounted.Describe();
        }
        catch
        {
            if (mounted is not null)
            {
                Unmount(mounted);
                await RemoveMountPointAsync(mounted, CancellationToken.None).ConfigureAwait(false);
            }

            await _access.ForgetAsync(pick.Key, CancellationToken.None).ConfigureAwait(false);
            await pick.Folder.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Asks the browser for its permission again; needs a recent user gesture.</summary>
    /// <returns>Whether the drive is connected now.</returns>
    public async ValueTask<bool> ReconnectAsync(string mountPoint, CancellationToken cancellationToken = default)
    {
        AuthenticatedPrincipal principal = RequirePrincipal();
        MountedDrive drive = RequireDrive(ParseMountPoint(mountPoint), principal);
        HostFolderPermission permission;
        try
        {
            permission = await drive.Provider.Folder.RequestPermissionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw new NetworkDriveException(NetworkDriveError.UserActivationRequired, exception.Message);
        }

        drive.Provider.SetConnected(permission == HostFolderPermission.Granted);
        Changed?.Invoke();
        return drive.Provider.IsConnected;
    }

    /// <summary>Unmounts a drive of the signed-in user (any drive for an administrator) and forgets it.</summary>
    public async ValueTask UnmountAsync(string mountPoint, CancellationToken cancellationToken = default)
    {
        AuthenticatedPrincipal principal = RequirePrincipal();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MountedDrive drive = RequireDrive(ParseMountPoint(mountPoint), principal);
            Unmount(drive);
            try
            {
                // Past this point the drive is gone: finish the cleanup even if the caller gives up.
                await _access.ForgetAsync(drive.Record.Key, CancellationToken.None).ConfigureAwait(false);
                await SaveTableAsync(principal, CancellationToken.None).ConfigureAwait(false);
                await RemoveMountPointAsync(drive, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await drive.Provider.Folder.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Mounts the signed-in user's drives again (at login): connected when the browser still grants
    /// its permission, disconnected otherwise. A drive whose handle is gone is dropped from the list.
    /// </summary>
    public async ValueTask RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (_principal() is not { } principal)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another user's drives do not outlive their session, even without a logout event.
            await UnmountWhereAsync(drive => drive.Provider.OwnerId != principal.LoginName.Value).ConfigureAwait(false);
            List<DriveRecord> records = await LoadTableAsync(principal, cancellationToken).ConfigureAwait(false);
            bool dropped = false;
            foreach (DriveRecord record in records)
            {
                VirtualPath path;
                try
                {
                    // The list is the user's own file: a mount point outside /mnt is never trusted.
                    path = ParseMountPoint(record.MountPoint);
                }
                catch (NetworkDriveException exception)
                {
                    _logger.LogWarning(exception, "Network drive {MountPoint} is not under /mnt; it is dropped.", record.MountPoint);
                    dropped = true;
                    continue;
                }

                if (Find(path) is not null)
                {
                    continue;
                }

                IHostFolder? folder = null;
                try
                {
                    folder = await _access.LoadAsync(record.Key, cancellationToken).ConfigureAwait(false);
                    if (folder is null)
                    {
                        dropped = true;
                        continue;
                    }

                    HostFolderPermission permission = await folder.QueryPermissionAsync(cancellationToken).ConfigureAwait(false);
                    Mount(record with { MountPoint = path.Value }, folder, principal, permission == HostFolderPermission.Granted);
                    folder = null;
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or FormatException)
                {
                    _logger.LogWarning(exception, "Network drive {MountPoint} could not be mounted again.", record.MountPoint);
                }
                finally
                {
                    if (folder is not null)
                    {
                        await folder.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }

            if (dropped)
            {
                await SaveTableAsync(principal, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Unmounts every drive (at logout); the list and the handles stay for the next login.</summary>
    public async ValueTask UnmountAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await UnmountWhereAsync(static _ => true).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Unmounts the matching drives, keeping their list and handles; the caller holds the gate.</summary>
    private async ValueTask UnmountWhereAsync(Func<MountedDrive, bool> predicate)
    {
        MountedDrive[] drives;
        lock (_mounted)
        {
            drives = [.. _mounted.Values.Where(predicate)];
        }

        foreach (MountedDrive drive in drives)
        {
            Unmount(drive);
            await drive.Provider.Folder.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool Sees(AuthenticatedPrincipal principal, MountedDrive drive) =>
        drive.Provider.OwnerId == principal.LoginName.Value || principal.Authority >= AppAuthority.Administrator;

    private void Observe(Task task, string operation) =>
        task.ContinueWith(
            completed => _logger.LogWarning(completed.Exception, "Network drives: the {Operation} after a session change failed.", operation),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (IDisposable subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _gate.Dispose();
    }

    private IFileSystemProvider RootProvider => _router.Resolve(VirtualPath.Parse("/")).Mount.Provider;

    private MountedDrive Mount(DriveRecord record, IHostFolder folder, AuthenticatedPrincipal principal, bool connected)
    {
        VirtualPath path = VirtualPath.Parse(record.MountPoint);
        HostFolderFileSystemProvider provider = new(
            path, folder, principal.LoginName.Value, principal.PrimaryGroupId.ToString(), connected, _time);
        _router.Mount(new FileSystemMount(path, provider, isProtectedRoot: true));
        MountedDrive drive = new(record, provider);
        lock (_mounted)
        {
            _mounted[path.Value] = drive;
        }

        Changed?.Invoke();
        return drive;
    }

    private void Unmount(MountedDrive drive)
    {
        _router.Unmount(drive.Provider.MountPath);
        lock (_mounted)
        {
            _mounted.Remove(drive.Provider.MountPath.Value);
        }

        Changed?.Invoke();
    }

    private MountedDrive RequireDrive(VirtualPath mountPoint, AuthenticatedPrincipal principal)
    {
        MountedDrive? drive;
        lock (_mounted)
        {
            _mounted.TryGetValue(mountPoint.Value, out drive);
        }

        if (drive is null)
        {
            throw new NetworkDriveException(NetworkDriveError.NotMounted, $"{mountPoint}: not mounted");
        }

        if (!Sees(principal, drive))
        {
            throw new NetworkDriveException(NetworkDriveError.NotOwner, $"{mountPoint}: must be the owner or an administrator");
        }

        return drive;
    }

    private AuthenticatedPrincipal RequirePrincipal() =>
        _principal() ?? throw new NetworkDriveException(NetworkDriveError.NotSignedIn, "No user is signed in.");

    /// <summary>A mount point is a direct child of <c>/mnt</c> (ADR 0181, section 4).</summary>
    private static VirtualPath ParseMountPoint(string value)
    {
        VirtualPath path;
        try
        {
            path = VirtualPath.Parse(value);
        }
        catch (FormatException)
        {
            throw new NetworkDriveException(NetworkDriveError.InvalidMountPoint, $"{value}: invalid mount point");
        }

        int separator = path.Value.LastIndexOf('/');
        if (separator <= 0 || path.Value[..separator] != MountRoot.Value)
        {
            throw new NetworkDriveException(NetworkDriveError.InvalidMountPoint, $"{value}: a network drive is mounted in /mnt");
        }

        return path;
    }

    private async ValueTask EnsureMountRootAsync(IFileSystemProvider root, CancellationToken cancellationToken)
    {
        FileSystemAuthorizationContext system = Context("system", "system");
        FileSystemResult<FileSystemEntrySnapshot> existing = await root.StatAsync(
            new FileSystemStatRequest(MountRoot, FileSystemLinkBehavior.NoFollow), system, cancellationToken).ConfigureAwait(false);
        if (existing.Succeeded)
        {
            return;
        }

        FileSystemMutationResult created = await root.CreateAsync(
            new FileSystemCreateRequest(MountRoot, FileSystemEntryKind.Directory, FileSystemPermissions.FromMode(0x1ED)),
            system,
            cancellationToken).ConfigureAwait(false);
        if (!created.Succeeded && created.Transaction.Error?.Code != FileSystemErrorCode.AlreadyExists)
        {
            throw new IOException($"Cannot create {MountRoot}: {created.Transaction.Error?.Code}.");
        }
    }

    /// <summary>Checks or creates a chosen mount point; returns whether it was created.</summary>
    private async ValueTask<bool> PrepareMountPointAsync(
        IFileSystemProvider root,
        VirtualPath path,
        AuthenticatedPrincipal principal,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemEntrySnapshot> existing = await root.StatAsync(
            new FileSystemStatRequest(path, FileSystemLinkBehavior.NoFollow), context, cancellationToken).ConfigureAwait(false);
        if (!existing.Succeeded)
        {
            return await TryCreateMountPointAsync(root, path, context, cancellationToken).ConfigureAwait(false)
                ? true
                : throw new NetworkDriveException(NetworkDriveError.InvalidMountPoint, $"{path}: cannot create the mount point");
        }

        if (_router.Resolve(path).Mount.Path == path)
        {
            throw new NetworkDriveException(NetworkDriveError.InvalidMountPoint, $"{path}: already mounted");
        }

        FileSystemEntryMetadata metadata = existing.Value!.Metadata;
        bool owned = metadata.OwnerId == principal.LoginName.Value || principal.Authority >= AppAuthority.Administrator;
        FileSystemResult<FileSystemDirectorySnapshot> listing = metadata.Kind == FileSystemEntryKind.Directory
            ? await root.EnumerateAsync(new FileSystemEnumerateRequest(path), context, cancellationToken).ConfigureAwait(false)
            : FileSystemResult<FileSystemDirectorySnapshot>.Failure(
                new FileSystemError(FileSystemOperation.Enumerate, FileSystemErrorCode.NotDirectory, path));
        if (!owned || !listing.Succeeded || listing.Value!.Entries.Count > 0)
        {
            throw new NetworkDriveException(NetworkDriveError.InvalidMountPoint, $"{path}: not an empty folder of yours");
        }

        return false;
    }

    /// <summary>Creates <c>/mnt/&lt;name&gt;</c>, or <c>/mnt/&lt;name&gt;-2</c> and so on when taken.</summary>
    private async ValueTask<VirtualPath> CreateFreeMountPointAsync(
        IFileSystemProvider root,
        string folderName,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken)
    {
        string name = SafeName(folderName);
        for (int attempt = 1; attempt < 100; attempt++)
        {
            VirtualPath candidate = VirtualPath.Parse($"{MountRoot.Value}/{(attempt == 1 ? name : $"{name}-{attempt}")}");
            if (await TryCreateMountPointAsync(root, candidate, context, cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        throw new NetworkDriveException(NetworkDriveError.InvalidMountPoint, $"{MountRoot}: no free mount point for '{name}'");
    }

    private static async ValueTask<bool> TryCreateMountPointAsync(
        IFileSystemProvider root,
        VirtualPath path,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken)
    {
        FileSystemMutationResult created = await root.CreateAsync(
            new FileSystemCreateRequest(path, FileSystemEntryKind.Directory, FileSystemPermissions.FromMode(MountPointMode)),
            context,
            cancellationToken).ConfigureAwait(false);
        return created.Succeeded;
    }

    private async ValueTask RemoveMountPointAsync(MountedDrive drive, CancellationToken cancellationToken)
    {
        if (!drive.Record.CreatedMountPoint)
        {
            return;
        }

        IFileSystemProvider root = RootProvider;
        FileSystemAuthorizationContext system = Context("system", "system");
        VirtualPath path = drive.Provider.MountPath;
        FileSystemResult<FileSystemEntrySnapshot> entry = await root.StatAsync(
            new FileSystemStatRequest(path, FileSystemLinkBehavior.NoFollow), system, cancellationToken).ConfigureAwait(false);
        FileSystemResult<FileSystemEntrySnapshot> parent = await root.StatAsync(
            new FileSystemStatRequest(MountRoot, FileSystemLinkBehavior.NoFollow), system, cancellationToken).ConfigureAwait(false);
        if (entry.Succeeded && parent.Succeeded)
        {
            // Not recursive: a mount point something was written into while unmounted stays.
            await root.DeleteAsync(
                new FileSystemDeleteRequest(path, entry.Value!.Metadata.Revision, parent.Value!.Metadata.Revision),
                system,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A folder name usable under <c>/mnt</c>; <c>drive</c> when nothing is left.</summary>
    private static string SafeName(string folderName)
    {
        string trimmed = new([.. folderName.Where(static c => c is not ('/' or '\\') && !char.IsControl(c))]);
        trimmed = trimmed.Trim();
        try
        {
            return trimmed is "" or "." or ".." ? "drive" : FileSystemEntryName.Parse(trimmed).Value;
        }
        catch (FormatException)
        {
            return "drive";
        }
    }

    private async ValueTask<List<DriveRecord>> LoadTableAsync(AuthenticatedPrincipal principal, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemContentReadHandle> read = await RootProvider.ReadAsync(
            new FileSystemReadRequest(TablePath(principal)), Context(principal), cancellationToken).ConfigureAwait(false);
        if (!read.Succeeded)
        {
            return [];
        }

        await using FileSystemContentReadHandle handle = read.Value!;
        try
        {
            DriveTable? table = await JsonSerializer.DeserializeAsync(
                handle.Content, NetworkDriveJsonContext.Default.DriveTable, cancellationToken).ConfigureAwait(false);
            return table?.Drives is { } drives ? [.. drives] : [];
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "{Path} is not valid JSON; no network drive is mounted again.", TableRelativePath);
            return [];
        }
    }

    private async ValueTask SaveTableAsync(AuthenticatedPrincipal principal, CancellationToken cancellationToken)
    {
        DriveRecord[] records;
        lock (_mounted)
        {
            records =
            [
                .. _mounted.Values
                    .Where(drive => drive.Provider.OwnerId == principal.LoginName.Value)
                    .OrderBy(static drive => drive.Record.MountPoint, StringComparer.Ordinal)
                    .Select(static drive => drive.Record)
            ];
        }

        IFileSystemProvider root = RootProvider;
        FileSystemAuthorizationContext context = Context(principal);
        VirtualPath path = TablePath(principal);
        FileSystemResult<FileSystemEntrySnapshot> existing = await root.StatAsync(
            new FileSystemStatRequest(path, FileSystemLinkBehavior.NoFollow), context, cancellationToken).ConfigureAwait(false);
        if (!existing.Succeeded)
        {
            FileSystemMutationResult created = await root.CreateAsync(
                new FileSystemCreateRequest(path, FileSystemEntryKind.File, FileSystemPermissions.FromMode(TableMode)),
                context,
                cancellationToken).ConfigureAwait(false);
            if (!created.Succeeded && created.Transaction.Error?.Code != FileSystemErrorCode.AlreadyExists)
            {
                _logger.LogWarning("{Path} cannot be created: {Code}.", path, created.Transaction.Error?.Code);
                return;
            }
        }

        byte[] content = JsonSerializer.SerializeToUtf8Bytes(new DriveTable(1, records), NetworkDriveJsonContext.Default.DriveTable);
        FileSystemMutationResult written = await root.WriteAsync(
            new FileSystemWriteRequest(path), new JsonContent(content), context, cancellationToken).ConfigureAwait(false);
        if (!written.Succeeded)
        {
            _logger.LogWarning("{Path} cannot be written: {Code}.", path, written.Transaction.Error?.Code);
        }
    }

    private static VirtualPath TablePath(AuthenticatedPrincipal principal) =>
        VirtualPath.Parse($"{principal.HomePath ?? $"/home/{principal.LoginName.Value}"}/{TableRelativePath}");

    private FileSystemAuthorizationContext Context(AuthenticatedPrincipal principal) =>
        Context(principal.LoginName.Value, principal.PrimaryGroupId.ToString());

    private FileSystemAuthorizationContext Context(string userId, string groupId) => new(
        new AppOperationContext
        {
            AppId = "org.hackeros.kernel",
            UserId = userId,
            UserAuthority = AppAuthority.System,
            GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
            IsSystemOperation = true
        },
        [groupId],
        _time.GetUtcNow().ToUniversalTime());

    private sealed record MountedDrive(DriveRecord Record, HostFolderFileSystemProvider Provider)
    {
        public NetworkDrive Describe() => new(Provider.MountPath, Record.Name, Provider.OwnerId, Provider.IsConnected);
    }

    private sealed class JsonContent(byte[] content) : IFileSystemContentSource
    {
        public FileSystemContentDescriptor Descriptor { get; } = FileSystemContentDescriptor.Text("application/json");

        public long? Length => content.LongLength;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false));
    }
}

/// <summary>One line of <c>network-drives.json</c>.</summary>
/// <param name="MountPoint">Mount point.</param>
/// <param name="Name">Name of the host folder.</param>
/// <param name="Key">Key of the handle in the browser's handle store.</param>
/// <param name="CreatedMountPoint">Whether mounting created the mount point, so unmounting removes it.</param>
internal sealed record DriveRecord(string MountPoint, string Name, string Key, bool CreatedMountPoint);

/// <summary>On-disk shape of <c>~/.local/state/hackeros/network-drives.json</c>.</summary>
/// <param name="SchemaVersion">Format version, 1.</param>
/// <param name="Drives">The user's drives, by mount point.</param>
internal sealed record DriveTable(int SchemaVersion, IReadOnlyList<DriveRecord> Drives);

/// <summary>Source-generated JSON for the drive list.</summary>
[JsonSerializable(typeof(DriveTable))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
internal sealed partial class NetworkDriveJsonContext : JsonSerializerContext;
