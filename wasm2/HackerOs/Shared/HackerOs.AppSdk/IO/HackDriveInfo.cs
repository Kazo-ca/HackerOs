namespace HackerOs.AppSdk.IO;

/// <summary>Kind of a drive, the subset of <c>System.IO.DriveType</c> HackerOS produces.</summary>
public enum HackDriveType
{
    /// <summary>A fixed disk: the IndexedDB disk behind <c>/</c>.</summary>
    Fixed,

    /// <summary>A network drive: a folder of the host computer mounted in <c>/mnt</c> (ADR 0181).</summary>
    Network
}

/// <summary>
/// What the OS knows about one sized mount, as an app instance receives it from
/// <see cref="IAppDriveGateway"/>: the raw <c>statfs</c> answer behind <see cref="HackDriveInfo"/>.
/// </summary>
/// <param name="Name">Mount point, e.g. <c>/</c>.</param>
/// <param name="DriveFormat">Filesystem type from <c>/proc/mounts</c>, e.g. <c>idbfs</c>.</param>
/// <param name="VolumeLabel">Device name, e.g. <c>hda</c>.</param>
/// <param name="TotalSize">Size of the disk, in bytes.</param>
/// <param name="AvailableFreeSpace">Free bytes, never negative.</param>
public sealed record HackDriveSnapshot(
    string Name,
    string DriveFormat,
    string VolumeLabel,
    long TotalSize,
    long AvailableFreeSpace)
{
    /// <summary>Gets the kind of drive; <see cref="HackDriveType.Fixed"/> unless set.</summary>
    public HackDriveType DriveType { get; init; } = HackDriveType.Fixed;

    /// <summary>Gets whether the drive can be read; a network drive is not until the browser grants its permission.</summary>
    public bool IsReady { get; init; } = true;
}

/// <summary>
/// Space of the mounted disks as one app instance sees it (ADR 0057, section 7), behind
/// <see cref="HackDriveInfo"/>. Reached through <see cref="IAppExecutionContext.Drives"/>. Needs no
/// capability, like <c>df</c> under Linux.
/// </summary>
public interface IAppDriveGateway
{
    /// <summary>Gets every mount that has a size: <c>/</c> and the network drives.</summary>
    ValueTask<IReadOnlyList<HackDriveSnapshot>> GetDrivesAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets whether the browser can mount a folder of the host computer (ADR 0181).</summary>
    ValueTask<bool> IsHostFolderSupportedAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    /// <summary>
    /// Opens the browser's folder dialog and mounts the chosen folder as a network drive;
    /// <see langword="null"/> when the user closed the dialog (ADR 0181, section 7).
    /// </summary>
    ValueTask<HackDriveSnapshot?> MountHostFolderAsync(string? mountPoint, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This drive gateway cannot mount host folders.");

    /// <summary>Asks the browser for its permission on a network drive again; whether it is ready now.</summary>
    ValueTask<bool> ReconnectAsync(string mountPoint, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This drive gateway cannot mount host folders.");

    /// <summary>Unmounts a network drive.</summary>
    ValueTask UnmountAsync(string mountPoint, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This drive gateway cannot mount host folders.");
}

/// <summary>
/// Space and format of a mounted disk, modeled on <c>System.IO.DriveInfo</c> (ADR 0057, section 7).
/// Under Linux the space of a disk is not a file but a <c>statfs</c> call; this is its .NET-shaped
/// equivalent. No capability is needed.
/// </summary>
/// <example>
/// <code>
/// foreach (HackDriveInfo drive in await HackDriveInfo.GetDrivesAsync())
/// {
///     Console.WriteLine($"{drive.Name} {drive.DriveFormat} {drive.AvailableFreeSpace}/{drive.TotalSize}");
/// }
/// </code>
/// </example>
/// <remarks>
/// Unlike <c>DriveInfo</c>, the values are read once, when the instance is created: the browser
/// only answers asynchronously, so a property cannot query it. Call <see cref="GetDrivesAsync"/>
/// again for fresh numbers.
/// </remarks>
public sealed class HackDriveInfo
{
    private HackDriveInfo(HackDriveSnapshot snapshot)
    {
        Name = snapshot.Name;
        DriveFormat = snapshot.DriveFormat;
        VolumeLabel = snapshot.VolumeLabel;
        TotalSize = snapshot.TotalSize;
        AvailableFreeSpace = Math.Max(0, snapshot.AvailableFreeSpace);
        DriveType = snapshot.DriveType;
        IsReady = snapshot.IsReady;
    }

    /// <summary>Gets the mount point, e.g. <c>/</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the filesystem type, e.g. <c>idbfs</c>.</summary>
    public string DriveFormat { get; }

    /// <summary>Gets the kind of drive: <see cref="HackDriveType.Fixed"/> for <c>/</c>, <see cref="HackDriveType.Network"/> for a host folder.</summary>
    public HackDriveType DriveType { get; }

    /// <summary>Gets whether the drive is ready; a network drive is not until the browser grants its permission again.</summary>
    public bool IsReady { get; }

    /// <summary>Gets the device name, e.g. <c>hda</c>.</summary>
    public string VolumeLabel { get; }

    /// <summary>Gets the size of the drive, in bytes.</summary>
    public long TotalSize { get; }

    /// <summary>Gets the free space available to the app, in bytes; never negative.</summary>
    public long AvailableFreeSpace { get; }

    /// <summary>Gets the total free space, in bytes; HackerOS reserves no blocks, so it equals <see cref="AvailableFreeSpace"/>.</summary>
    public long TotalFreeSpace => AvailableFreeSpace;

    /// <summary>Gets the root directory of the drive.</summary>
    public HackDirectoryInfo RootDirectory => new(Name);

    /// <summary>Gets every mounted drive that has a size: <c>/</c> and the network drives.</summary>
    /// <exception cref="HackExecutionContextMissingException">Called outside an app's execution context.</exception>
    public static async Task<HackDriveInfo[]> GetDrivesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<HackDriveSnapshot> drives = await HackExecutionContext.Current.Drives
            .GetDrivesAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. drives.Select(static drive => new HackDriveInfo(drive))];
    }

    /// <summary>Gets whether this browser can mount a folder of the host computer (Chrome and Edge on a computer).</summary>
    /// <exception cref="HackExecutionContextMissingException">Called outside an app's execution context.</exception>
    public static async Task<bool> IsHostFolderSupportedAsync(CancellationToken cancellationToken = default) =>
        await HackExecutionContext.Current.Drives.IsHostFolderSupportedAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Opens the browser's folder dialog and mounts the folder the user chooses as a network drive
    /// (ADR 0181): at <c>/mnt/&lt;folder name&gt;</c>, or at <paramref name="mountPoint"/>
    /// (<c>/mnt/&lt;name&gt;</c>, an empty folder of the user). Call it from a click or a key press:
    /// the browser opens its dialog only after a user gesture.
    /// </summary>
    /// <returns>The new drive, or <see langword="null"/> when the user closed the dialog.</returns>
    /// <exception cref="NotSupportedException">The browser cannot (Firefox, Safari, mobile).</exception>
    /// <exception cref="InvalidOperationException">No recent user gesture, or nobody is signed in.</exception>
    /// <exception cref="IOException">The mount point is not usable.</exception>
    public static async Task<HackDriveInfo?> MountHostFolderAsync(string? mountPoint = null, CancellationToken cancellationToken = default)
    {
        HackDriveSnapshot? drive = await HackExecutionContext.Current.Drives
            .MountHostFolderAsync(mountPoint, cancellationToken)
            .ConfigureAwait(false);
        return drive is null ? null : new HackDriveInfo(drive);
    }

    /// <summary>
    /// Asks the browser again for its permission on a network drive that is not ready (after a
    /// reload). Call it from a click or a key press.
    /// </summary>
    /// <returns>Whether the drive is ready now.</returns>
    /// <exception cref="IOException">No network drive is mounted there.</exception>
    /// <exception cref="UnauthorizedAccessException">The drive belongs to another user.</exception>
    public static async Task<bool> ReconnectAsync(string mountPoint, CancellationToken cancellationToken = default) =>
        await HackExecutionContext.Current.Drives.ReconnectAsync(mountPoint, cancellationToken).ConfigureAwait(false);

    /// <summary>Unmounts a network drive; the folder of the host computer is not touched.</summary>
    /// <exception cref="IOException">No network drive is mounted there.</exception>
    /// <exception cref="UnauthorizedAccessException">The drive belongs to another user and the caller is not an administrator.</exception>
    public static async Task UnmountAsync(string mountPoint, CancellationToken cancellationToken = default) =>
        await HackExecutionContext.Current.Drives.UnmountAsync(mountPoint, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public override string ToString() => Name;
}
