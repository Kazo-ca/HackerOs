using System.Text.RegularExpressions;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>Shares manifest-compatible app-ID syntax between validation and mutation boundaries.</summary>
internal static partial class StartMenuAppIdSyntax
{
    internal static bool IsValid(string? appId) => AppIdPattern().IsMatch(appId ?? string.Empty);

    [GeneratedRegex("^[a-z][a-z0-9-]*(?:\\.[a-z][a-z0-9-]*){2,}$", RegexOptions.CultureInvariant)]
    private static partial Regex AppIdPattern();
}
