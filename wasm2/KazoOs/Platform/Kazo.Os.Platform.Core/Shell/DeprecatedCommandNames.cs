namespace Kazo.Os.Platform.Core.Shell;

/// <summary>
/// Command names from before the rename to Kazo OS (ADR 0228 §2). Each stays an alias of its new
/// command for one minor version: the shell writes one line on standard error, then runs the new
/// command, like a distribution that keeps a renamed binary as a symbolic link with a warning.
/// </summary>
internal static class DeprecatedCommandNames
{
    private static readonly Dictionary<string, string> Replacements = new(StringComparer.Ordinal)
    {
        ["hackpkg"] = "kazopkg",
        ["hackbackup"] = "kazobackup",
    };

    /// <summary>Gets the new name of a deprecated command name.</summary>
    /// <param name="name">The name the command was run under.</param>
    /// <param name="replacement">The name to use instead.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> is deprecated.</returns>
    public static bool TryGetReplacement(string name, out string replacement) =>
        Replacements.TryGetValue(name, out replacement!);
}
