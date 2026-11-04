using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.ComponentProviders;

/// <summary>
/// The file <c>/etc/hackeros/components.config</c> (ADR 0156 §2): which app provides each component
/// contract an administrator chooses, such as the file view and the file dialogs.
/// </summary>
/// <remarks>
/// <para>
/// One line per chosen contract in the <c>[providers]</c> section, keyed by the contract name:
/// </para>
/// <code>
/// [providers]
/// hackeros.file-view=com.example.files
/// hackeros.file-dialogs=com.example.files
/// </code>
/// <para>
/// A missing key means the File Explorer (or the order of ADR 0063 §3 when it is missing); a fresh
/// installation has no key and may have no file. The file belongs to <c>system:system</c> like every
/// document of <c>/etc/hackeros</c>, so only an administrator changes it. The contract names contain
/// dots, so the file is read line by line (<see cref="ConfigDocumentLines"/>) rather than through a
/// declared schema; a key that is not a contract an administrator chooses is ignored, and so is an
/// app the catalog does not know, when the OS resolves the contract.
/// </para>
/// </remarks>
public static class ComponentProvidersDocument
{
    /// <summary>The file name under <c>/etc/hackeros</c>.</summary>
    public const string FileName = "components.config";

    /// <summary>The section holding the choices.</summary>
    public const string ProvidersGroup = "providers";

    /// <summary>The absolute path, for messages and logs.</summary>
    public const string SystemPath = OsSettingsDocument.SystemDirectory + "/" + FileName;

    /// <summary>Gets the file in the system location.</summary>
    public static OsSettingsFile File { get; } = new(OsSettingsLocation.System, FileName);

    /// <summary>The comment a new file starts with, written the first time an administrator chooses.</summary>
    public static string Header { get; } =
        "# /etc/hackeros/components.config\n"
        + "# Which app provides the OS's file view and file dialogs (ADR 0156). A missing key means\n"
        + "# the File Explorer. Choose in Settings > OS components: a third-party app needs an\n"
        + "# administrator's approval, since its code then runs inside every app that shows files.\n";

    /// <summary>
    /// Reads the choices of <paramref name="content"/>: the last valid line of a contract wins, as in
    /// any <c>.config</c> document.
    /// </summary>
    /// <param name="content">The file's text, or <see langword="null"/> when it does not exist.</param>
    /// <returns>Contract name to app id, ordinal keys.</returns>
    public static IReadOnlyDictionary<string, string> Parse(string? content)
    {
        Dictionary<string, string> choices = new(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(content))
        {
            return choices;
        }

        foreach (ConfigLine line in ConfigDocumentLines.Parse(content))
        {
            if (line.Kind == ConfigLineKind.KeyValue
                && !line.InMalformedSection
                && string.Equals(line.Group, ProvidersGroup, StringComparison.Ordinal)
                && line.Key is { } contract
                && IsChosenByAdministrator(contract)
                && line.Value?.Trim() is { Length: > 0 } appId)
            {
                choices[contract] = appId;
            }
        }

        return choices;
    }

    /// <summary>
    /// Writes or removes the choice of one contract, keeping every other line as it is (ADR 0044 §7).
    /// </summary>
    /// <param name="content">The current text, or <see langword="null"/> when the file does not exist.</param>
    /// <param name="contract">The contract.</param>
    /// <param name="appId">The chosen app, or <see langword="null"/> to remove the line (back to the default).</param>
    /// <returns>The new text; <see langword="null"/> when a removal finds no file (nothing to write).</returns>
    public static string? Edit(string? content, string contract, string? appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contract);
        if (appId is null)
        {
            return content is null ? null : ConfigDocumentEditor.RemoveValue(content, ProvidersGroup, contract);
        }

        return ConfigDocumentEditor.SetValue(string.IsNullOrEmpty(content) ? Header : content, ProvidersGroup, contract, appId);
    }

    /// <summary>Gets whether an administrator chooses the provider of <paramref name="contract"/> (ADR 0156).</summary>
    /// <param name="contract">Contract name.</param>
    public static bool IsChosenByAdministrator(string contract) =>
        AppComponentContracts.GetProviderScope(contract) == AppComponentProviderScope.AdministratorSelected;
}
