using HackerOs.AppSdk.IO;

namespace HackerOs.Tests.Support;

/// <summary>
/// In-memory <see cref="IAppDriveGateway"/>: the OS disk plus network drives (ADR 0181) a test
/// mounts through the SDK, with the browser's dialog answer chosen by the test.
/// </summary>
public sealed class FakeAppDriveGateway : IAppDriveGateway
{
    private readonly List<HackDriveSnapshot> _drives = [new("/", "idbfs", "hda", 1000, 400)];

    /// <summary>Gets or sets whether the browser has a folder dialog.</summary>
    public bool Supported { get; set; } = true;

    /// <summary>Folder name the next dialog returns; <see langword="null"/> closes the dialog.</summary>
    public string? NextPick { get; set; }

    /// <summary>Exception the next mount, reconnect or unmount throws.</summary>
    public Exception? NextFailure { get; set; }

    /// <summary>Gets or sets whether a reconnect is granted.</summary>
    public bool GrantOnReconnect { get; set; } = true;

    /// <summary>Gets the mounted drives.</summary>
    public IReadOnlyList<HackDriveSnapshot> Drives => _drives;

    /// <summary>Adds a network drive, connected or not.</summary>
    public FakeAppDriveGateway WithNetworkDrive(string mountPoint, string name, bool ready = true)
    {
        _drives.Add(Network(mountPoint, name, ready));
        return this;
    }

    public ValueTask<IReadOnlyList<HackDriveSnapshot>> GetDrivesAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<HackDriveSnapshot>>([.. _drives]);

    public ValueTask<bool> IsHostFolderSupportedAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Supported);

    public ValueTask<HackDriveSnapshot?> MountHostFolderAsync(string? mountPoint, CancellationToken cancellationToken = default)
    {
        ThrowPending();
        if (!Supported)
        {
            throw new NotSupportedException("hostfs: not supported by this browser");
        }

        if (NextPick is not { } name)
        {
            return ValueTask.FromResult<HackDriveSnapshot?>(null);
        }

        HackDriveSnapshot drive = Network(mountPoint ?? $"/mnt/{name}", name, ready: true);
        _drives.Add(drive);
        return ValueTask.FromResult<HackDriveSnapshot?>(drive);
    }

    public ValueTask<bool> ReconnectAsync(string mountPoint, CancellationToken cancellationToken = default)
    {
        ThrowPending();
        int index = IndexOf(mountPoint);
        _drives[index] = _drives[index] with { IsReady = GrantOnReconnect };
        return ValueTask.FromResult(GrantOnReconnect);
    }

    public ValueTask UnmountAsync(string mountPoint, CancellationToken cancellationToken = default)
    {
        ThrowPending();
        _drives.RemoveAt(IndexOf(mountPoint));
        return ValueTask.CompletedTask;
    }

    private int IndexOf(string mountPoint)
    {
        int index = _drives.FindIndex(drive => drive.Name == mountPoint && drive.DriveType == HackDriveType.Network);
        return index >= 0 ? index : throw new IOException($"{mountPoint}: not mounted");
    }

    private void ThrowPending()
    {
        if (NextFailure is { } failure)
        {
            NextFailure = null;
            throw failure;
        }
    }

    private static HackDriveSnapshot Network(string mountPoint, string name, bool ready) =>
        new(mountPoint, "hostfs", name, 0, 0) { DriveType = HackDriveType.Network, IsReady = ready };
}
