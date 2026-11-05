namespace Kazo.Os.Windowing.Abstractions;

/// <summary>
/// Identifies the owning app instance of a window as an opaque token, independent of any host's
/// process/instance model. A host adapter derives this value from its own domain identity (e.g. a
/// running app instance) and is responsible for mapping it back when it needs the original
/// identity; the engine only ever compares two owner IDs for equality.
/// </summary>
public readonly record struct WindowOwnerId
{
    private WindowOwnerId(Guid value) => Value = value;

    /// <summary>Gets the underlying non-empty identifier.</summary>
    public Guid Value { get; }

    /// <summary>Creates a window owner identifier from a non-empty GUID.</summary>
    public static WindowOwnerId FromGuid(Guid value) => value != Guid.Empty
        ? new WindowOwnerId(value)
        : throw new ArgumentOutOfRangeException(nameof(value), "Window owner ID cannot be empty.");

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}
