using System.Globalization;

namespace HackerOs.Windowing.Abstractions;

/// <summary>
/// Identifies one screen of the extended desktop (ADR 0178 §2, §5) by the number the user sees:
/// screen 1 is the main tab, and every other screen is a display surface that takes the smallest
/// free number and keeps it as long as it exists. The default value is <see cref="Primary"/>, so a
/// window or a command that never names a screen stays on screen 1.
/// </summary>
public readonly record struct DisplayId
{
    // Stored as Number - 1 so that default(DisplayId) is screen 1.
    private readonly int _offset;

    private DisplayId(int offset) => _offset = offset;

    /// <summary>Gets screen 1, the main tab.</summary>
    public static DisplayId Primary => default;

    /// <summary>Gets the screen number shown to the user, starting at 1.</summary>
    public int Number => _offset + 1;

    /// <summary>Gets whether this is screen 1.</summary>
    public bool IsPrimary => _offset == 0;

    /// <summary>Creates the identity of screen <paramref name="number"/>.</summary>
    /// <param name="number">The screen number, 1 or more.</param>
    public static DisplayId FromNumber(int number) => number >= 1
        ? new DisplayId(number - 1)
        : throw new ArgumentOutOfRangeException(nameof(number), "Screen numbers start at 1.");

    /// <inheritdoc />
    public override string ToString() => Number.ToString(CultureInfo.InvariantCulture);
}
