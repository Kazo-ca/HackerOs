using System.Globalization;

namespace Kazo.Os.Simulation.Abstractions.Sessions;

/// <summary>Identifies one local user independently of its mutable login or display name.</summary>
public readonly record struct LocalUserId
{
    private LocalUserId(Guid value) => Value = value;

    /// <summary>Gets the opaque 128-bit identifier value.</summary>
    public Guid Value { get; }

    /// <summary>Creates a user identifier from a non-empty value.</summary>
    public static LocalUserId FromGuid(Guid value) => value == Guid.Empty
        ? throw new ArgumentException("Local user IDs cannot be empty.", nameof(value))
        : new LocalUserId(value);

    /// <summary>Returns canonical lowercase identifier text.</summary>
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>Identifies one local group independently of its mutable name.</summary>
public readonly record struct LocalGroupId
{
    private LocalGroupId(Guid value) => Value = value;

    /// <summary>Gets the opaque 128-bit identifier value.</summary>
    public Guid Value { get; }

    /// <summary>Creates a group identifier from a non-empty value.</summary>
    public static LocalGroupId FromGuid(Guid value) => value == Guid.Empty
        ? throw new ArgumentException("Local group IDs cannot be empty.", nameof(value))
        : new LocalGroupId(value);

    /// <summary>Returns canonical lowercase identifier text.</summary>
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>Identifies one authenticated session for its lifetime.</summary>
public readonly record struct SessionId
{
    private SessionId(Guid value) => Value = value;

    /// <summary>Gets the opaque 128-bit identifier value.</summary>
    public Guid Value { get; }

    /// <summary>Creates a session identifier from a non-empty value.</summary>
    public static SessionId FromGuid(Guid value) => value == Guid.Empty
        ? throw new ArgumentException("Session IDs cannot be empty.", nameof(value))
        : new SessionId(value);

    /// <summary>Returns canonical lowercase identifier text.</summary>
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>Identifies one Kazo OS installation/profile, used by app/device settings scope.</summary>
public readonly record struct InstallationId
{
    private InstallationId(Guid value) => Value = value;

    /// <summary>Gets the opaque 128-bit identifier value.</summary>
    public Guid Value { get; }

    /// <summary>Creates an installation identifier from a non-empty value.</summary>
    public static InstallationId FromGuid(Guid value) => value == Guid.Empty
        ? throw new ArgumentException("Installation IDs cannot be empty.", nameof(value))
        : new InstallationId(value);

    /// <summary>Returns canonical lowercase identifier text.</summary>
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>Identifies one physical/browser device, which may host multiple installations over time.</summary>
public readonly record struct DeviceId
{
    private DeviceId(Guid value) => Value = value;

    /// <summary>Gets the opaque 128-bit identifier value.</summary>
    public Guid Value { get; }

    /// <summary>Creates a device identifier from a non-empty value.</summary>
    public static DeviceId FromGuid(Guid value) => value == Guid.Empty
        ? throw new ArgumentException("Device IDs cannot be empty.", nameof(value))
        : new DeviceId(value);

    /// <summary>Returns canonical lowercase identifier text.</summary>
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
