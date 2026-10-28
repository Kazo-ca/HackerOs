using System.Globalization;
using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.Settings;

namespace HackerOs.Platform.Core.Shell;

/// <summary>
/// Where and how big a secondary screen's host window was (ADR 0178 §4, ADR 0180): the position of
/// its frame on the host's virtual desktop and the size of its content, in CSS pixels: the values
/// <c>window.open</c> takes back (<c>left</c>, <c>top</c>, <c>width</c>, <c>height</c>). Written as
/// <c>left,top,width,height</c> in <c>screens.config</c>.
/// </summary>
/// <param name="Left">Left edge of the window frame (<c>screenX</c>).</param>
/// <param name="Top">Top edge of the window frame (<c>screenY</c>).</param>
/// <param name="Width">Width of the window's content (<c>innerWidth</c>).</param>
/// <param name="Height">Height of the window's content (<c>innerHeight</c>).</param>
public readonly record struct ScreenGeometry(int Left, int Top, int Width, int Height)
{
    /// <summary>Smallest width a remembered window keeps; anything smaller is not a real placement.</summary>
    public const int MinimumWidth = 200;

    /// <summary>Smallest height a remembered window keeps.</summary>
    public const int MinimumHeight = 150;

    /// <summary>Reads <c>left,top,width,height</c>; <see langword="false"/> for anything else or a too small window.</summary>
    /// <param name="text">The value of a <c>[screens]</c> line.</param>
    /// <param name="geometry">The geometry read.</param>
    public static bool TryParse(string? text, out ScreenGeometry geometry)
    {
        geometry = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
        {
            return false;
        }

        int[] values = new int[4];
        for (int index = 0; index < 4; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out values[index]))
            {
                return false;
            }
        }

        if (values[2] < MinimumWidth || values[3] < MinimumHeight)
        {
            return false;
        }

        geometry = new ScreenGeometry(values[0], values[1], values[2], values[3]);
        return true;
    }

    /// <summary>Writes <c>left,top,width,height</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Left},{Top},{Width},{Height}");
}

/// <summary>
/// Which windows the taskbar of screen 1 lists (ADR 0179 §1): every window, or only the windows of
/// screen 1. The taskbars of the other screens always list the windows of their screen.
/// </summary>
public enum PrimaryTaskbarMode
{
    /// <summary>Every window of every screen (<c>primary-taskbar=all</c>, the default).</summary>
    All,

    /// <summary>Only the windows of screen 1 (<c>primary-taskbar=own</c>).</summary>
    Own,
}

/// <summary>
/// The screens file (ADR 0179 §7, ADR 0180): <c>/etc/hackeros/screens.config</c> and
/// <c>~/.config/hackeros/screens.config</c>. This type declares the <c>[screens]</c> section the
/// screen service writes: <c>open</c> lists the secondary screens open in the last session (for
/// « Restaurer les écrans ») and each numbered line remembers where that screen's window was. The
/// <c>[taskbar]</c> section holds <c>primary-taskbar</c> (ADR 0179 §1).
/// </summary>
/// <example>
/// <code>
/// [taskbar]
/// primary-taskbar = own
///
/// [screens]
/// open = 2,3
/// 2 = 1920,0,1920,1080
/// 3 = -1280,0,1280,1024
/// </code>
/// </example>
public static class ScreensSettingsDocuments
{
    /// <summary>Document id, the file name without <c>.config</c>.</summary>
    public const string DocumentId = "screens";

    /// <summary>Section of the screens of the last session and their placement.</summary>
    public const string ScreensGroup = "screens";

    /// <summary>Key of the list of the screens open in the last session.</summary>
    public const string OpenKey = "open";

    /// <summary>Section of the taskbar settings.</summary>
    public const string TaskbarGroup = "taskbar";

    /// <summary>Key of what the taskbar of screen 1 lists: <c>all</c> or <c>own</c>.</summary>
    public const string PrimaryTaskbarKey = "primary-taskbar";

    /// <summary>Value of <see cref="PrimaryTaskbarMode.All"/>.</summary>
    public const string PrimaryTaskbarAll = "all";

    /// <summary>Value of <see cref="PrimaryTaskbarMode.Own"/>.</summary>
    public const string PrimaryTaskbarOwn = "own";

    /// <summary>Highest screen number the file remembers: the screen service opens no more screens.</summary>
    public const int MaxScreenNumber = 9;

    /// <summary>Gets the declared schema: <c>primary-taskbar</c>, <c>open</c> and one line per secondary screen number.</summary>
    public static SettingsSchema Schema { get; } = new(
        schemaVersion: 1,
        fields:
        [
            new SettingFieldDeclaration(PrimaryTaskbarKey, SettingValueType.String, PrimaryTaskbarAll, SettingSensitivity.Public, Group: TaskbarGroup),
            new SettingFieldDeclaration(OpenKey, SettingValueType.String, string.Empty, SettingSensitivity.Public, Group: ScreensGroup),
            .. Enumerable.Range(2, MaxScreenNumber - 1).Select(static number => new SettingFieldDeclaration(
                number.ToString(CultureInfo.InvariantCulture), SettingValueType.String, string.Empty, SettingSensitivity.Public, Group: ScreensGroup)),
        ]);

    /// <summary>Gets the layered document.</summary>
    public static OsSettingsDocument Document { get; } = new(DocumentId, Schema);

    /// <summary>Gets the qualified key of the <c>primary-taskbar</c> line.</summary>
    public static string PrimaryTaskbarQualifiedKey => $"{TaskbarGroup}.{PrimaryTaskbarKey}";

    /// <summary>Gets the qualified key of the <c>open</c> line.</summary>
    public static string OpenQualifiedKey => $"{ScreensGroup}.{OpenKey}";

    /// <summary>Gets the qualified key of a screen's placement line.</summary>
    /// <param name="number">Screen number, 2 to <see cref="MaxScreenNumber"/>.</param>
    public static string GeometryQualifiedKey(int number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, MaxScreenNumber);
        return string.Create(CultureInfo.InvariantCulture, $"{ScreensGroup}.{number}");
    }

    /// <summary>
    /// Reads the screens open in the last session, in their order, without duplicates. A number that
    /// is not a secondary screen (1, 10, text) is skipped.
    /// </summary>
    /// <param name="snapshot">Snapshot of <see cref="Document"/>.</param>
    public static IReadOnlyList<int> ReadOpenScreens(LayeredConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.Values.TryGetValue(OpenQualifiedKey, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        List<int> numbers = [];
        foreach (string part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                && number is >= 2 and <= MaxScreenNumber
                && !numbers.Contains(number))
            {
                numbers.Add(number);
            }
        }

        return numbers;
    }

    /// <summary>Reads every remembered placement, by screen number; invalid lines are skipped.</summary>
    /// <param name="snapshot">Snapshot of <see cref="Document"/>.</param>
    public static IReadOnlyDictionary<int, ScreenGeometry> ReadGeometries(LayeredConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Dictionary<int, ScreenGeometry> geometries = [];
        for (int number = 2; number <= MaxScreenNumber; number++)
        {
            if (snapshot.Values.TryGetValue(GeometryQualifiedKey(number), out string? value)
                && ScreenGeometry.TryParse(value, out ScreenGeometry geometry))
            {
                geometries[number] = geometry;
            }
        }

        return geometries;
    }

    /// <summary>Writes the value of the <c>open</c> line.</summary>
    /// <param name="numbers">Open secondary screens, in order.</param>
    public static string FormatOpenScreens(IEnumerable<int> numbers)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        return string.Join(',', numbers.Select(static number => number.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Reads what the taskbar of screen 1 lists. <c>own</c> (any case) is <see cref="PrimaryTaskbarMode.Own"/>;
    /// anything else, or no line, keeps the default <see cref="PrimaryTaskbarMode.All"/>.
    /// </summary>
    /// <param name="snapshot">Snapshot of <see cref="Document"/>.</param>
    public static PrimaryTaskbarMode ReadPrimaryTaskbar(LayeredConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Values.TryGetValue(PrimaryTaskbarQualifiedKey, out string? value)
            && string.Equals(value?.Trim(), PrimaryTaskbarOwn, StringComparison.OrdinalIgnoreCase)
                ? PrimaryTaskbarMode.Own
                : PrimaryTaskbarMode.All;
    }

    /// <summary>Writes the value of the <c>primary-taskbar</c> line.</summary>
    /// <param name="mode">What the taskbar of screen 1 lists.</param>
    public static string FormatPrimaryTaskbar(PrimaryTaskbarMode mode) =>
        mode == PrimaryTaskbarMode.Own ? PrimaryTaskbarOwn : PrimaryTaskbarAll;
}
