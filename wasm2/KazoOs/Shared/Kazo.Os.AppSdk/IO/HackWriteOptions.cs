using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.AppSdk.IO;

/// <summary>
/// Advanced options for a <c>Hack*</c> write (ADR 0043, section 3). The defaults (no options)
/// give the convenient behavior: create with the umask, retry once on a concurrent change.
/// </summary>
public sealed record HackWriteOptions
{
    /// <summary>
    /// Gets the revision the caller last read. When set, the write commits only if the file is still
    /// at that revision and otherwise throws <see cref="HackFileConflictException"/> immediately, with
    /// no automatic retry: the strict conflict control a text editor needs before overwriting.
    /// </summary>
    public long? ExpectedRevision { get; init; }

    /// <summary>
    /// Gets the permission bits of the file if the write has to create it, instead of the umask
    /// default (<c>0644</c>). Ignored when the file already exists, like <c>open(O_CREAT, mode)</c>.
    /// Only the nine <c>rwx</c> bits are supported.
    /// </summary>
    public UnixFileMode? CreateMode { get; init; }
}

/// <summary>Converts between BCL <see cref="UnixFileMode"/> and the engine's <see cref="FileSystemPermissions"/>.</summary>
internal static class HackUnixFileMode
{
    /// <summary>Fixed phase 1 umask (<c>022</c>).</summary>
    public const int Umask = 0b000_010_010;

    /// <summary>Default mode of a file created by the facade: <c>0666 &amp; ~022 = 0644</c>.</summary>
    public static readonly FileSystemPermissions DefaultFile = FileSystemPermissions.FromMode((ushort)(0b110_110_110 & ~Umask));

    /// <summary>Default mode of a directory created by the facade: <c>0777 &amp; ~022 = 0755</c>.</summary>
    public static readonly FileSystemPermissions DefaultDirectory = FileSystemPermissions.FromMode((ushort)(0b111_111_111 & ~Umask));

    private const int PermissionBits = 0b111_111_111;

    /// <summary>Converts BCL permission bits; setuid/setgid/sticky are not supported by the engine.</summary>
    /// <exception cref="ArgumentException"><paramref name="mode"/> carries bits beyond <c>0777</c>.</exception>
    public static FileSystemPermissions ToPermissions(UnixFileMode mode, string parameterName)
    {
        int value = (int)mode;
        if ((value & ~PermissionBits) != 0)
        {
            throw new ArgumentException(
                "Only the owner/group/other read, write and execute bits (0777) are supported.", parameterName);
        }

        return FileSystemPermissions.FromMode((ushort)value);
    }

    /// <summary>Converts engine permissions to the identical BCL bit layout.</summary>
    public static UnixFileMode FromPermissions(FileSystemPermissions permissions) => (UnixFileMode)permissions.Mode;
}
