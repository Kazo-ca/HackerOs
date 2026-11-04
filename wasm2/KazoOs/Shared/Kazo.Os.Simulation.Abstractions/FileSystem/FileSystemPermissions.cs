namespace Kazo.Os.Simulation.Abstractions.FileSystem;

/// <summary>
/// Defines Linux-inspired read, write, and execute access for one subject class.
/// </summary>
[Flags]
public enum FileSystemAccess : byte
{
    /// <summary>No mode-based access.</summary>
    None = 0,

    /// <summary>Execute a file or traverse a directory.</summary>
    Execute = 1,

    /// <summary>Write file content or mutate directory children.</summary>
    Write = 2,

    /// <summary>Read file content or enumerate a directory.</summary>
    Read = 4
}

/// <summary>
/// Stores owner, group, and other access without embedding an entry-kind marker.
/// </summary>
public readonly record struct FileSystemPermissions
{
    private const FileSystemAccess AllAccess =
        FileSystemAccess.Read | FileSystemAccess.Write | FileSystemAccess.Execute;

    /// <summary>Initializes a validated permission triple.</summary>
    /// <param name="owner">Owner access.</param>
    /// <param name="group">Group access.</param>
    /// <param name="other">Other-user access.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value contains unsupported bits.</exception>
    public FileSystemPermissions(
        FileSystemAccess owner,
        FileSystemAccess group,
        FileSystemAccess other)
    {
        Validate(owner, nameof(owner));
        Validate(group, nameof(group));
        Validate(other, nameof(other));

        Owner = owner;
        Group = group;
        Other = other;
    }

    /// <summary>Gets owner access.</summary>
    public FileSystemAccess Owner { get; }

    /// <summary>Gets owning-group access.</summary>
    public FileSystemAccess Group { get; }

    /// <summary>Gets all-other-user access.</summary>
    public FileSystemAccess Other { get; }

    /// <summary>Gets the equivalent nine-bit Unix-style mode.</summary>
    public ushort Mode => (ushort)(((byte)Owner << 6) | ((byte)Group << 3) | (byte)Other);

    /// <summary>Creates permissions from a nine-bit Unix-style mode.</summary>
    /// <param name="mode">Permission bits in the inclusive range 0000 through 0777.</param>
    /// <returns>The permission triple.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The mode contains unsupported bits.</exception>
    public static FileSystemPermissions FromMode(ushort mode)
    {
        if ((mode & ~0x01FF) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Permission mode cannot exceed 0777.");
        }

        return new FileSystemPermissions(
            (FileSystemAccess)((mode >> 6) & 0x07),
            (FileSystemAccess)((mode >> 3) & 0x07),
            (FileSystemAccess)(mode & 0x07));
    }

    /// <summary>Returns a three-digit octal permission mode.</summary>
    /// <returns>The octal mode without an entry-kind prefix.</returns>
    public override string ToString() => Convert.ToString(Mode, 8).PadLeft(3, '0');

    private static void Validate(FileSystemAccess access, string parameterName)
    {
        if ((access & ~AllAccess) != 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                access,
                "Filesystem access contains unsupported permission bits.");
        }
    }
}