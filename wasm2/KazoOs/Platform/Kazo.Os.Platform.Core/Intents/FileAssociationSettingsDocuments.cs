using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Intents;

/// <summary>
/// Defines the administrator file-association document <c>/etc/kazoos/file-associations.json</c>,
/// per `P1-APP-008A` and ADR 0044 section 8.
/// </summary>
/// <remarks>
/// A strict-JSON, whole-file exception to the line-based <c>.config</c> format (ADR 0011), system
/// layer only. Each entry stores an explicit, administrator-configured default handler for one
/// extension or media type. Apps that are not named here are still discoverable candidates through
/// their own manifest's <see cref="App.Abstractions.FileHandlerManifest"/> declarations
/// (see <see cref="FileAssociationIndex"/> and <see cref="FileAssociationResolver"/>).
/// </remarks>
public static class FileAssociationSettingsDocuments
{
    /// <summary>
    /// Gets the historical settings-store identity, kept only as the stable record id of the
    /// FileAssociations sync domain (ADR 0033) and for the migration of ADR 0044.
    /// </summary>
    public static SettingsDocumentKey Key { get; } = SettingsDocumentKey.ForOsAdmin("file-associations");

    /// <summary>Gets the file.</summary>
    public static OsSettingsFile File { get; } = new(OsSettingsLocation.System, "file-associations.json");

    /// <summary>
    /// Gets the built-in content used while the file is absent or invalid: it makes
    /// <c>org.kazoos.file-explorer</c> the default handler for <c>inode/directory</c> (`INT-009`),
    /// so directory-open intents resolve to a <see cref="FileHandlerResolutionStatus.ConfiguredDefault"/>
    /// out of the box instead of falling through to the chooser, <c>org.kazoos.browser</c> the
    /// default web browser, <c>x-scheme-handler/http</c> and <c>https</c> (ADR 0096, section 3), and
    /// <c>org.kazoos.kazo-paint</c> the default handler of <c>edit</c> for the images it reads
    /// (ADR 0103 §1), so "Edit" keeps opening Kazo Paint once another app can also open images.
    /// </summary>
    public const string DefaultContent =
        "{\"schemaVersion\":1,\"associations\":[" +
        "{\"mediaType\":\"inode/directory\",\"appId\":\"org.kazoos.file-explorer\",\"actions\":[\"open\"]}," +
        "{\"mediaType\":\"x-scheme-handler/http\",\"appId\":\"org.kazoos.browser\",\"actions\":[\"open\"]}," +
        "{\"mediaType\":\"x-scheme-handler/https\",\"appId\":\"org.kazoos.browser\",\"actions\":[\"open\"]}," +
        "{\"extension\":\".png\",\"appId\":\"org.kazoos.kazo-paint\",\"actions\":[\"edit\"]}," +
        "{\"extension\":\".jpg\",\"appId\":\"org.kazoos.kazo-paint\",\"actions\":[\"edit\"]}," +
        "{\"extension\":\".jpeg\",\"appId\":\"org.kazoos.kazo-paint\",\"actions\":[\"edit\"]}," +
        "{\"extension\":\".bmp\",\"appId\":\"org.kazoos.kazo-paint\",\"actions\":[\"edit\"]}," +
        "{\"extension\":\".gif\",\"appId\":\"org.kazoos.kazo-paint\",\"actions\":[\"edit\"]}," +
        "{\"extension\":\".webp\",\"appId\":\"org.kazoos.kazo-paint\",\"actions\":[\"edit\"]}" +
        "]}";

    /// <summary>Reads the effective document (the file, or <see cref="DefaultContent"/>).</summary>
    /// <param name="settings">OS settings service.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static ValueTask<string> ReadAsync(IOsSettingsService settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.ReadValidatedFileAsync(File, new FileAssociationSettingsValidator(), DefaultContent, cancellationToken);
    }
}
