using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Policy;

/// <summary>A remembered decision about one dangerous capability of one app (ADR 0049 section 4).</summary>
public enum AppPermissionDecision
{
    /// <summary><c>allow</c>: only honored in the machine file.</summary>
    Allow = 0,

    /// <summary><c>deny</c>: machine-wide in <c>/etc</c>, for one user in <c>~/.config</c>.</summary>
    Deny = 1
}

/// <summary>
/// Layout of the two <c>app-permissions.config</c> files (ADR 0049 section 4): the machine file
/// <c>/etc/kazoos/app-permissions.config</c> (<c>system:system 0644</c>, the only source of an
/// <c>allow</c>) and the user file <c>~/.config/kazoos/app-permissions.config</c> (only
/// <c>deny</c> lines, valid for that user alone).
/// </summary>
/// <remarks>
/// Each line is <c>&lt;appId&gt;:&lt;capability&gt;=allow|deny</c> in the <c>[permissions]</c>
/// section. The files have no schema: they are read with <see cref="ConfigDocumentFormat.Parse"/>
/// and edited line by line with <see cref="ConfigDocumentEditor"/>, so comments and any line an
/// administrator wrote by hand with <c>sudo nano</c> survive every OS write.
/// </remarks>
public static class AppPermissionsDocument
{
    /// <summary>File name shared by both layers.</summary>
    public const string FileName = "app-permissions.config";

    /// <summary>Section holding every decision.</summary>
    public const string Section = "permissions";

    /// <summary>Serialized <see cref="AppPermissionDecision.Allow"/>.</summary>
    public const string AllowValue = "allow";

    /// <summary>Serialized <see cref="AppPermissionDecision.Deny"/>.</summary>
    public const string DenyValue = "deny";

    /// <summary>Gets the machine file.</summary>
    public static OsSettingsFile MachineFile { get; } = new(OsSettingsLocation.System, FileName);

    /// <summary>Gets the session user's file.</summary>
    public static OsSettingsFile UserFile { get; } = new(OsSettingsLocation.UserConfig, FileName);

    /// <summary>Gets the commented machine file a fresh installation ships.</summary>
    public static string DefaultMachineContent { get; } =
        "# /etc/kazoos/app-permissions.config\n"
        + "# Capacités dangereuses approuvées (allow) ou refusées (deny) par un administrateur\n"
        + "# pour toute la machine (ADR 0049). Une ligne par décision :\n"
        + "#   <appId>:<capacité>=allow|deny\n"
        + "# Écrit par l'OS après une approbation, ou à la main avec sudo nano.\n"
        + $"[{Section}]\n";

    /// <summary>Header written at the top of a new user file.</summary>
    public static string DefaultUserContent { get; } =
        "# ~/.config/kazoos/app-permissions.config\n"
        + "# Capacités dangereuses que vous avez refusées (deny) ; elles valent pour vous seul.\n"
        + "# Un allow ici est ignoré : seul /etc/kazoos/app-permissions.config peut autoriser.\n"
        + $"[{Section}]\n";

    /// <summary>Builds the key of one decision line.</summary>
    /// <param name="appId">App identifier.</param>
    /// <param name="capability">Capability identifier.</param>
    public static string FormatKey(string appId, string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        return $"{appId}:{capability}";
    }

    /// <summary>Serializes a decision value.</summary>
    /// <param name="decision">Decision to serialize.</param>
    public static string ToValue(AppPermissionDecision decision) =>
        decision == AppPermissionDecision.Allow ? AllowValue : DenyValue;

    /// <summary>Sets (or replaces) one decision line, keeping every other line.</summary>
    /// <param name="content">Current content, or <see langword="null"/> for a new file.</param>
    /// <param name="defaultContent">Header used when <paramref name="content"/> is absent.</param>
    /// <param name="appId">App identifier.</param>
    /// <param name="capability">Capability identifier.</param>
    /// <param name="decision">Decision to record.</param>
    public static string SetDecision(
        string? content, string defaultContent, string appId, string capability, AppPermissionDecision decision) =>
        ConfigDocumentEditor.SetValue(
            string.IsNullOrEmpty(content) ? defaultContent : content,
            Section,
            FormatKey(appId, capability),
            ToValue(decision));

    /// <summary>Removes one decision line; unchanged when absent.</summary>
    /// <param name="content">Current content.</param>
    /// <param name="appId">App identifier.</param>
    /// <param name="capability">Capability identifier.</param>
    public static string RemoveDecision(string? content, string appId, string capability) =>
        ConfigDocumentEditor.RemoveValue(content, Section, FormatKey(appId, capability));
}

/// <summary>
/// Parsed decisions of the machine and user <c>app-permissions.config</c> files (ADR 0049 section 4).
/// </summary>
/// <remarks>
/// Parsing never fails: a malformed line, an unknown capability, a value other than
/// <c>allow</c>/<c>deny</c> or an <c>allow</c> in the user file is ignored and reported in
/// <see cref="Warnings"/> (the store logs them to <c>/var/log/syslog</c>). A line naming a normal
/// capability is ignored without warning, since only dangerous capabilities need a decision.
/// Lines naming a system app are kept here and ignored by <see cref="DangerousCapabilityEvaluator"/>.
/// </remarks>
public sealed class AppPermissionDecisions
{
    private readonly Dictionary<(string AppId, string Capability), AppPermissionDecision> _machine;
    private readonly HashSet<(string AppId, string Capability)> _userDenials;

    private AppPermissionDecisions(
        Dictionary<(string AppId, string Capability), AppPermissionDecision> machine,
        HashSet<(string AppId, string Capability)> userDenials,
        IReadOnlyList<string> warnings)
    {
        _machine = machine;
        _userDenials = userDenials;
        Warnings = warnings;
    }

    /// <summary>Gets decisions with no line at all (both files absent).</summary>
    public static AppPermissionDecisions Empty { get; } = new([], [], []);

    /// <summary>Gets one warning line per ignored line, formatted like ADR 0044 settings warnings.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Parses the two files.</summary>
    /// <param name="machineContent">Content of the machine file, or <see langword="null"/> when absent.</param>
    /// <param name="userContent">Content of the session user's file, or <see langword="null"/> when absent or no session.</param>
    /// <param name="machinePath">Machine file path used in warnings.</param>
    /// <param name="userPath">User file path used in warnings.</param>
    public static AppPermissionDecisions Parse(
        string? machineContent,
        string? userContent,
        string machinePath = "/etc/kazoos/app-permissions.config",
        string userPath = "~/.config/kazoos/app-permissions.config")
    {
        Dictionary<(string, string), AppPermissionDecision> machine = [];
        HashSet<(string, string)> userDenials = [];
        List<string> warnings = [];

        foreach (((string appId, string capability), AppPermissionDecision decision) in ParseFile(machineContent, machinePath, warnings))
        {
            machine[(appId, capability)] = decision;
        }

        foreach (((string appId, string capability), AppPermissionDecision decision) in ParseFile(userContent, userPath, warnings))
        {
            if (decision == AppPermissionDecision.Allow)
            {
                // Only the machine file can authorize (ADR 0049 section 4).
                warnings.Add($"settings: {userPath}: '{appId}:{capability}=allow' ignored, only {machinePath} can allow");
                continue;
            }

            userDenials.Add((appId, capability));
        }

        return new AppPermissionDecisions(machine, userDenials, warnings);
    }

    /// <summary>Gets the machine decision for one app capability, or <see langword="null"/> when none.</summary>
    /// <param name="appId">App identifier.</param>
    /// <param name="capability">Capability identifier.</param>
    public AppPermissionDecision? GetMachineDecision(string appId, string capability) =>
        _machine.TryGetValue((appId, capability), out AppPermissionDecision decision) ? decision : null;

    /// <summary>Gets whether the session user refused one app capability for themselves.</summary>
    /// <param name="appId">App identifier.</param>
    /// <param name="capability">Capability identifier.</param>
    public bool IsDeniedByUser(string appId, string capability) => _userDenials.Contains((appId, capability));

    private static IEnumerable<KeyValuePair<(string AppId, string Capability), AppPermissionDecision>> ParseFile(
        string? content, string path, List<string> warnings)
    {
        if (string.IsNullOrEmpty(content))
        {
            yield break;
        }

        ConfigDocumentParseResult parsed = ConfigDocumentFormat.Parse(content);
        foreach (ConfigDocumentParseError error in parsed.Errors)
        {
            warnings.Add($"settings: {path}:{error.LineNumber}: {error.Code}, line ignored");
        }

        string prefix = AppPermissionsDocument.Section + ".";
        foreach ((string qualifiedKey, string rawValue) in parsed.Values)
        {
            if (!qualifiedKey.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (!string.Equals(qualifiedKey, "schemaVersion", StringComparison.Ordinal))
                {
                    warnings.Add($"settings: {path}: '{qualifiedKey}' is outside [{AppPermissionsDocument.Section}], line ignored");
                }

                continue;
            }

            string key = qualifiedKey[prefix.Length..];
            int separator = key.LastIndexOf(':');
            if (separator <= 0 || separator == key.Length - 1)
            {
                warnings.Add($"settings: {path}: '{key}' is not <appId>:<capability>, line ignored");
                continue;
            }

            string appId = key[..separator];
            string capability = key[(separator + 1)..];
            if (!AppCapabilities.IsKnown(capability))
            {
                warnings.Add($"settings: {path}: unknown capability '{capability}', line ignored");
                continue;
            }

            if (!AppCapabilities.IsDangerous(capability))
            {
                // A normal capability needs no decision: ignored without effect.
                continue;
            }

            string value = rawValue.Trim();
            AppPermissionDecision decision;
            if (string.Equals(value, AppPermissionsDocument.AllowValue, StringComparison.Ordinal))
            {
                decision = AppPermissionDecision.Allow;
            }
            else if (string.Equals(value, AppPermissionsDocument.DenyValue, StringComparison.Ordinal))
            {
                decision = AppPermissionDecision.Deny;
            }
            else
            {
                warnings.Add($"settings: {path}: '{key}={value}' expects allow or deny, line ignored");
                continue;
            }

            yield return new((appId, capability), decision);
        }
    }
}
