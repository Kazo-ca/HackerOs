using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.Settings;

namespace HackerOs.Platform.Core.Shell;

/// <summary>
/// Defines the per-user start-menu document <c>~/.config/hackeros/start-menu.json</c> (ADR 0044
/// section 8): strict JSON, user layer only, one file per user in that user's own home.
/// </summary>
/// <remarks>
/// The file keeps the historical shape <c>{"schemaVersion":1,"profiles":{"&lt;userId&gt;":{"pinnedAppIds":[...]}}}</c>
/// with a single profile, so the validator and codec are shared with the migration from the old
/// aggregated <c>/etc/hackeros/start-menu.json</c> store record.
/// </remarks>
public static class StartMenuSettingsDocuments
{
    /// <summary>The maximum number of ordered quick-launch pins stored for one local user.</summary>
    public const int MaximumPinnedAppCount = 64;

    /// <summary>Gets the historical settings-store identity, read only by the migration.</summary>
    public static SettingsDocumentKey Key { get; } = SettingsDocumentKey.ForOsAdmin("start-menu");

    /// <summary>Gets the per-user file.</summary>
    public static OsSettingsFile File { get; } = new(OsSettingsLocation.UserConfig, "start-menu.json");

    /// <summary>
    /// Property of a profile listing the default pins already added once (ADR 0212 §6), so an app the
    /// user unpinned is not pinned again.
    /// </summary>
    public const string DefaultPinsAppliedProperty = "defaultPinsApplied";

    /// <summary>
    /// Gets the apps pinned by default (ADR 0188 §1): to every new profile, and added once to the
    /// existing profiles. The user may unpin them.
    /// </summary>
    public static IReadOnlyList<string> DefaultPinnedAppIds { get; } = ["org.hackeros.store"];

    /// <summary>Gets the clean-profile document containing no pins.</summary>
    public const string EmptyDocumentContent = "{\"schemaVersion\":1,\"profiles\":{}}";
}
