using System.Globalization;
using System.Text;
using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.Settings;

namespace HackerOs.Platform.Core.Shell;

/// <summary>A window manager command reachable from the keyboard (ADR 0095 §3).</summary>
public enum WindowKeyboardAction
{
    /// <summary>Switch to the next window; the switcher stays open while Alt is held.</summary>
    SwitchNext,

    /// <summary>Switch to the previous window.</summary>
    SwitchPrevious,

    /// <summary>Close the active window, through its close confirmation (ADR 0006).</summary>
    Close,

    /// <summary>Open the active window's menu.</summary>
    WindowMenu,

    /// <summary>Move the active window with the arrow keys; Enter validates, Escape cancels.</summary>
    Move,

    /// <summary>Resize the active window with the arrow keys; Enter validates, Escape cancels.</summary>
    Resize,

    /// <summary>Minimize the active window.</summary>
    Minimize,

    /// <summary>Maximize the active window.</summary>
    Maximize,

    /// <summary>Restore a maximized window, or minimize a normal one (Super+Down).</summary>
    RestoreOrMinimize,

    /// <summary>Maximize a normal window, restore a maximized one.</summary>
    ToggleMaximize,

    /// <summary>Tile the active window on the left half of the work area.</summary>
    SnapLeft,

    /// <summary>Tile the active window on the right half of the work area.</summary>
    SnapRight,

    /// <summary>
    /// Show HackerOS full screen in the host browser, or leave full screen. Full screen is where the
    /// host grants the keyboard lock, hence the Linux keys.
    /// </summary>
    ToggleFullScreen,

    /// <summary>Move the active window to the previous screen, by number (ADR 0178 §6).</summary>
    MoveToPreviousScreen,

    /// <summary>Move the active window to the next screen, by number (ADR 0178 §6).</summary>
    MoveToNextScreen,
}

/// <summary>Which of the two default key sets a binding belongs to (ADR 0095 §3).</summary>
public enum WindowKeyBindingSet
{
    /// <summary>
    /// The usual Linux desktop keys (Alt+Tab, Alt+F4, Super+arrows). The host system catches them
    /// before the page, so they are only active while HackerOS holds the keyboard lock (full screen).
    /// </summary>
    Linux,

    /// <summary>Replacement keys the host browser lets through, always active.</summary>
    Fallback,
}

/// <summary>
/// One key combination: modifiers and one key, written like <c>Alt+Shift+Tab</c> in
/// <c>keybindings.config</c>.
/// </summary>
/// <remarks>
/// The key is a letter, a digit, <c>F1</c>–<c>F24</c>, one of <c>Tab</c>, <c>Space</c>,
/// <c>Escape</c>, <c>Enter</c>, <c>Backspace</c>, <c>Delete</c>, <c>Insert</c>, <c>Home</c>,
/// <c>End</c>, <c>PageUp</c>, <c>PageDown</c>, <c>Up</c>, <c>Down</c>, <c>Left</c>, <c>Right</c>, or a
/// physical key name such as <c>Backquote</c> (the key above Tab, whatever the layout prints on it).
/// Modifiers are <c>Ctrl</c>, <c>Alt</c>, <c>Shift</c> and <c>Super</c>.
/// </remarks>
/// <param name="Control">Whether Ctrl is held.</param>
/// <param name="Alt">Whether Alt is held.</param>
/// <param name="Shift">Whether Shift is held.</param>
/// <param name="Super">Whether the Super (Windows, Command) key is held.</param>
/// <param name="Key">Canonical key name.</param>
public sealed record KeyChord(bool Control, bool Alt, bool Shift, bool Super, string Key)
{
    private static readonly Dictionary<string, string> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tab"] = "Tab",
        ["Space"] = "Space",
        ["Escape"] = "Escape",
        ["Esc"] = "Escape",
        ["Enter"] = "Enter",
        ["Return"] = "Enter",
        ["Backspace"] = "Backspace",
        ["Delete"] = "Delete",
        ["Insert"] = "Insert",
        ["Home"] = "Home",
        ["End"] = "End",
        ["PageUp"] = "PageUp",
        ["PageDown"] = "PageDown",
        ["Up"] = "Up",
        ["Down"] = "Down",
        ["Left"] = "Left",
        ["Right"] = "Right",
        ["Backquote"] = "Backquote",
        ["Minus"] = "Minus",
        ["Equal"] = "Equal",
        ["BracketLeft"] = "BracketLeft",
        ["BracketRight"] = "BracketRight",
        ["Backslash"] = "Backslash",
        ["Semicolon"] = "Semicolon",
        ["Quote"] = "Quote",
        ["Comma"] = "Comma",
        ["Period"] = "Period",
        ["Slash"] = "Slash",
    };

    /// <summary>
    /// Gets the browser <c>KeyboardEvent.key</c> this chord matches, or <see langword="null"/> when it
    /// is matched on the physical key only (<see cref="DomCode"/>).
    /// </summary>
    public string? DomKey => Key switch
    {
        "Space" => " ",
        "Up" => "ArrowUp",
        "Down" => "ArrowDown",
        "Left" => "ArrowLeft",
        "Right" => "ArrowRight",
        "Backquote" or "Minus" or "Equal" or "BracketLeft" or "BracketRight" or "Backslash"
            or "Semicolon" or "Quote" or "Comma" or "Period" or "Slash" => null,
        _ when Key.Length == 1 => Key.ToLowerInvariant(),
        _ => Key,
    };

    /// <summary>
    /// Gets the browser <c>KeyboardEvent.code</c> (physical key) this chord falls back to, used for
    /// punctuation keys whose printed character depends on the layout, and for a letter when Alt
    /// turned it into another character (macOS Option).
    /// </summary>
    public string? DomCode => Key switch
    {
        _ when Key.Length == 1 && char.IsAsciiLetter(Key[0]) => "Key" + Key.ToUpperInvariant(),
        _ when Key.Length == 1 && char.IsAsciiDigit(Key[0]) => "Digit" + Key,
        "Space" => "Space",
        "Backquote" or "Minus" or "Equal" or "BracketLeft" or "BracketRight" or "Backslash"
            or "Semicolon" or "Quote" or "Comma" or "Period" or "Slash" => Key,
        _ => null,
    };

    /// <summary>Parses <c>Alt+Shift+Tab</c>; modifiers in any order, names case-insensitive.</summary>
    /// <param name="text">Chord text.</param>
    /// <param name="chord">The chord when the text is valid.</param>
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
        bool control = false, alt = false, shift = false, super = false;
        for (int index = 0; index < parts.Length - 1; index++)
        {
            switch (parts[index].ToLowerInvariant())
            {
                case "ctrl" or "control" when !control:
                    control = true;
                    break;
                case "alt" when !alt:
                    alt = true;
                    break;
                case "shift" when !shift:
                    shift = true;
                    break;
                case "super" or "meta" or "win" or "cmd" when !super:
                    super = true;
                    break;
                default:
                    return false;
            }
        }

        string? key = NormalizeKey(parts[^1]);
        if (key is null)
        {
            return false;
        }

        chord = new KeyChord(control, alt, shift, super, key);
        return true;
    }

    /// <summary>Parses a chord or throws.</summary>
    /// <param name="text">Chord text.</param>
    /// <exception cref="FormatException">The text is not a valid chord.</exception>
    public static KeyChord Parse(string text) =>
        TryParse(text, out KeyChord chord) ? chord : throw new FormatException($"'{text}' is not a key combination.");

    /// <summary>Whether a key event with these modifiers and DOM key/code is this chord.</summary>
    /// <param name="control">Ctrl held.</param>
    /// <param name="alt">Alt held.</param>
    /// <param name="shift">Shift held.</param>
    /// <param name="super">Super held.</param>
    /// <param name="domKey"><c>KeyboardEvent.key</c>.</param>
    /// <param name="domCode"><c>KeyboardEvent.code</c>.</param>
    public bool Matches(bool control, bool alt, bool shift, bool super, string? domKey, string? domCode)
    {
        if (control != Control || alt != Alt || shift != Shift || super != Super)
        {
            return false;
        }

        if (DomKey is { } key && domKey is not null)
        {
            if (string.Equals(key, domKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // A printable key whose character Alt changed (macOS Option+Q is "œ"): use the physical key.
            if (key.Length == 1 && domKey.Length == 1 && !char.IsAscii(domKey[0]))
            {
                return DomCode is { } fallback && string.Equals(fallback, domCode, StringComparison.Ordinal);
            }

            return false;
        }

        return DomKey is null && DomCode is { } code && string.Equals(code, domCode, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        StringBuilder builder = new();
        if (Control)
        {
            builder.Append("Ctrl+");
        }

        if (Alt)
        {
            builder.Append("Alt+");
        }

        if (Shift)
        {
            builder.Append("Shift+");
        }

        if (Super)
        {
            builder.Append("Super+");
        }

        return builder.Append(Key).ToString();
    }

    private static string? NormalizeKey(string key)
    {
        if (key.Length == 1 && (char.IsAsciiLetterOrDigit(key[0])))
        {
            return key.ToUpperInvariant();
        }

        if (key.Length == 1 && key[0] == '`')
        {
            return "Backquote";
        }

        if (key.Length is 2 or 3 && (key[0] is 'F' or 'f')
            && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int function)
            && function is >= 1 and <= 24)
        {
            return "F" + function.ToString(CultureInfo.InvariantCulture);
        }

        return NamedKeys.TryGetValue(key, out string? named) ? named : null;
    }
}

/// <summary>One shortcut of the window manager.</summary>
/// <param name="Action">Command run by the shortcut.</param>
/// <param name="Set">Key set the shortcut belongs to.</param>
/// <param name="Chord">Key combination.</param>
public sealed record WindowKeyBinding(WindowKeyboardAction Action, WindowKeyBindingSet Set, KeyChord Chord);

/// <summary>
/// The window manager's shortcuts file (ADR 0095 §3, ADR 0044): <c>/etc/hackeros/keybindings.config</c>
/// is the machine default and <c>~/.config/hackeros/keybindings.config</c> the user's choice, each with a
/// <c>[linux]</c> and a <c>[fallback]</c> section holding one line per action, for example
/// <c>close = Alt+F4</c>. A value may list several chords separated by commas; an empty value removes
/// the shortcut.
/// </summary>
public static class WindowKeyBindingsSettingsDocuments
{
    /// <summary>Document id, the file name without <c>.config</c>.</summary>
    public const string DocumentId = "keybindings";

    /// <summary>Section of the keys active only under the keyboard lock.</summary>
    public const string LinuxGroup = "linux";

    /// <summary>Section of the always-active replacement keys.</summary>
    public const string FallbackGroup = "fallback";

    private static readonly (WindowKeyboardAction Action, string Key, string Linux, string Fallback)[] Defaults =
    [
        (WindowKeyboardAction.SwitchNext, "switch-next", "Alt+Tab", "Alt+Backquote"),
        (WindowKeyboardAction.SwitchPrevious, "switch-previous", "Alt+Shift+Tab", "Alt+Shift+Backquote"),
        (WindowKeyboardAction.Close, "close", "Alt+F4", "Alt+Q"),
        (WindowKeyboardAction.WindowMenu, "window-menu", "Alt+Space", "Alt+F3"),
        (WindowKeyboardAction.Move, "move", "Alt+F7", "Alt+F7"),
        (WindowKeyboardAction.Resize, "resize", "Alt+F8", "Alt+F8"),
        (WindowKeyboardAction.Minimize, "minimize", "", "Alt+F9"),
        (WindowKeyboardAction.Maximize, "maximize", "Super+Up", ""),
        (WindowKeyboardAction.RestoreOrMinimize, "restore-or-minimize", "Super+Down", ""),
        (WindowKeyboardAction.ToggleMaximize, "toggle-maximize", "", "Alt+F10"),
        (WindowKeyboardAction.SnapLeft, "snap-left", "Super+Left", "Alt+Shift+Left"),
        (WindowKeyboardAction.SnapRight, "snap-right", "Super+Right", "Alt+Shift+Right"),
        (WindowKeyboardAction.ToggleFullScreen, "toggle-fullscreen", "", "Alt+F11"),
        (WindowKeyboardAction.MoveToPreviousScreen, "move-to-previous-screen", "Super+Shift+Left", "Ctrl+Alt+Shift+Left"),
        (WindowKeyboardAction.MoveToNextScreen, "move-to-next-screen", "Super+Shift+Right", "Ctrl+Alt+Shift+Right"),
    ];

    /// <summary>Gets the declared schema: one string key per action in each of the two sections.</summary>
    public static SettingsSchema Schema { get; } = new(
        schemaVersion: 1,
        fields:
        [
            .. Defaults.Select(static entry => new SettingFieldDeclaration(
                entry.Key, SettingValueType.String, entry.Linux, SettingSensitivity.Public, Group: LinuxGroup)),
            .. Defaults.Select(static entry => new SettingFieldDeclaration(
                entry.Key, SettingValueType.String, entry.Fallback, SettingSensitivity.Public, Group: FallbackGroup)),
        ]);

    /// <summary>Gets the layered document.</summary>
    public static OsSettingsDocument Document { get; } = new(DocumentId, Schema);

    /// <summary>Gets the file-name key of an action (<c>switch-next</c>…).</summary>
    /// <param name="action">Action.</param>
    public static string KeyOf(WindowKeyboardAction action) => Defaults.First(entry => entry.Action == action).Key;

    /// <summary>
    /// Gets the system-layer file the OS ships (<c>/etc/hackeros/keybindings.config</c>): every line
    /// commented out, documenting the defaults without pinning them, as Linux distributions ship
    /// their configuration files.
    /// </summary>
    public static string DefaultSystemFileContent
    {
        get
        {
            StringBuilder builder = new();
            builder.Append("# HackerOS window manager shortcuts (ADR 0095).\n");
            builder.Append("# Machine default; each user may override a line in ~/.config/hackeros/keybindings.config.\n");
            builder.Append("# Uncomment a line to change it. Several chords: separate them with commas. Empty: no shortcut.\n");
            builder.Append("# Modifiers: Ctrl, Alt, Shift, Super. Backquote is the key above Tab.\n");
            builder.Append("\n# Active only in full screen, once HackerOS holds the keyboard lock.\n[linux]\n");
            foreach ((_, string key, string linux, _) in Defaults)
            {
                builder.Append(CultureInfo.InvariantCulture, $"#{key} = {linux}\n");
            }

            builder.Append("\n# Always active: keys the host browser lets through.\n[fallback]\n");
            foreach ((_, string key, _, string fallback) in Defaults)
            {
                builder.Append(CultureInfo.InvariantCulture, $"#{key} = {fallback}\n");
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// Reads every binding of a merged snapshot. A chord that does not parse is skipped and reported
    /// in <paramref name="problems"/>, so one typo never removes the other shortcuts.
    /// </summary>
    /// <param name="snapshot">Snapshot of <see cref="Document"/>.</param>
    /// <param name="problems">One line per skipped chord, for the OS log.</param>
    public static IReadOnlyList<WindowKeyBinding> ToBindings(LayeredConfigSnapshot snapshot, out IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        List<WindowKeyBinding> bindings = [];
        List<string> errors = [];
        foreach ((WindowKeyboardAction action, string key, _, _) in Defaults)
        {
            Read(LinuxGroup, WindowKeyBindingSet.Linux);
            Read(FallbackGroup, WindowKeyBindingSet.Fallback);

            void Read(string group, WindowKeyBindingSet set)
            {
                string qualified = $"{group}.{key}";
                if (!snapshot.Values.TryGetValue(qualified, out string? value))
                {
                    return;
                }

                foreach (string text in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (KeyChord.TryParse(text, out KeyChord chord))
                    {
                        bindings.Add(new WindowKeyBinding(action, set, chord));
                    }
                    else
                    {
                        errors.Add($"keybindings: [{group}] {key}: '{text}' is not a key combination, ignored");
                    }
                }
            }
        }

        problems = errors;
        return bindings;
    }

    /// <summary>Gets the default bindings, used before the file is read and when it cannot be.</summary>
    public static IReadOnlyList<WindowKeyBinding> DefaultBindings { get; } =
    [
        .. Defaults.SelectMany(static entry => new[]
            {
                (entry.Action, Set: WindowKeyBindingSet.Linux, Text: entry.Linux),
                (entry.Action, Set: WindowKeyBindingSet.Fallback, Text: entry.Fallback),
            })
            .Where(static entry => entry.Text.Length > 0)
            .Select(static entry => new WindowKeyBinding(entry.Action, entry.Set, KeyChord.Parse(entry.Text))),
    ];
}
