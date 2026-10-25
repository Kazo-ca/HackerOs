using System.Text.RegularExpressions;

namespace HackerOs.App.Abstractions;

/// <summary>
/// Validates app-declared topic-permission capability identifiers — the mechanism an app uses to gate
/// its own shared messaging channel behind a permission other apps must be explicitly granted, per
/// <c>doc/en/wasm/dev/adr/0040-declared-topic-permissions.md</c>. Kept deliberately separate from the fixed,
/// curated <see cref="AppCapabilities"/> OS-resource catalog (ADR 0003 governs that catalog's exact-match
/// semantics; this is a parallel, app-declared space, not an addition to it).
/// </summary>
/// <remarks>
/// A well-formed identifier has the shape <c>topic-publish:app/{appId}/{segment}[/{segment}...]</c> or
/// <c>topic-subscribe:app/{appId}/{segment}[/{segment}...]</c>. Callers never hand-type these strings —
/// production code builds them from an already-validated
/// <c>HackerOs.Simulation.Abstractions.Events.TopicName</c> via the
/// <c>ToPublishPermission()</c>/<c>ToSubscribePermission()</c> extension methods; this type exists so the
/// (lower-layer) manifest validator and capability grant model can check the resulting string's shape
/// without depending on that (higher-layer) messaging project.
/// </remarks>
public static partial class TopicPermissions
{
    /// <summary>Prefix identifying a publish-side topic permission.</summary>
    public const string PublishPrefix = "topic-publish:";

    /// <summary>Prefix identifying a subscribe-side topic permission.</summary>
    public const string SubscribePrefix = "topic-subscribe:";

    [GeneratedRegex(
        @"^(?:topic-publish|topic-subscribe):app/([a-z][a-z0-9-]*(?:\.[a-z][a-z0-9-]*){2,})/[a-z0-9-]+(?:/[a-z0-9-]+)*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>Determines whether <paramref name="capability"/> has the well-formed topic-permission shape.</summary>
    public static bool IsWellFormed(string capability) => !string.IsNullOrEmpty(capability) && Pattern().IsMatch(capability);

    /// <summary>
    /// Determines whether a well-formed <paramref name="capability"/> is rooted under <paramref name="appId"/>'s
    /// own topic namespace — i.e. only <paramref name="appId"/> may declare it.
    /// </summary>
    public static bool IsOwnedByApp(string capability, string appId)
    {
        if (string.IsNullOrEmpty(appId))
        {
            return false;
        }

        Match match = Pattern().Match(capability ?? string.Empty);
        return match.Success && string.Equals(match.Groups[1].Value, appId, StringComparison.Ordinal);
    }
}
