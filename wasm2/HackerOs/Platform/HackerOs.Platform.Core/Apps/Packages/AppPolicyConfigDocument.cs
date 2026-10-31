using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.Settings;

namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>
/// <c>/etc/hackeros/app-policy.config</c> (ADR 0189 §5): the administrator's block list. An app
/// blocked by its ID, its category or a capability it asks for no longer installs for one user; it
/// needs the two-step system install of ADR 0189 §4.
/// </summary>
/// <remarks>
/// <code>
/// [blocked-apps]
/// org.example.chat=true
/// [blocked-categories]
/// social=true
/// [blocked-capabilities]
/// network.real.access=true
/// </code>
/// Created at boot from the host's build (<see cref="AppPackageDefaults"/>) when it does not exist;
/// read without a schema, a bad line is skipped with a warning.
/// </remarks>
public static class AppPolicyConfigDocument
{
    /// <summary>Section of blocked app IDs.</summary>
    public const string AppsSection = "blocked-apps";

    /// <summary>Section of blocked categories (<see cref="PresentationManifest.Category"/>).</summary>
    public const string CategoriesSection = "blocked-categories";

    /// <summary>Section of blocked capabilities (<see cref="AppManifest.Capabilities"/>).</summary>
    public const string CapabilitiesSection = "blocked-capabilities";

    /// <summary>File name under <c>/etc/hackeros</c>.</summary>
    public const string FileName = "app-policy.config";

    /// <summary>Machine file, <c>system:system 0644</c>.</summary>
    public static VirtualPath MachinePath { get; } = VirtualPath.Parse($"/etc/hackeros/{FileName}");

    /// <summary>Content written when the file does not exist yet, with the standard defaults (nothing blocked).</summary>
    public static string DefaultContent { get; } = BuildDefaultContent(AppPackageDefaults.Standard);

    /// <summary>Gets the section of one kind of rule.</summary>
    public static string SectionOf(AppBlockKind kind) => kind switch
    {
        AppBlockKind.App => AppsSection,
        AppBlockKind.Category => CategoriesSection,
        AppBlockKind.Capability => CapabilitiesSection,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>Builds the content written when the file does not exist yet.</summary>
    /// <param name="defaults">The block list the host was built with.</param>
    public static string BuildDefaultContent(AppPackageDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        StringBuilder text = new();
        text.Append("# Apps an administrator blocks for a personal install (ADR 0189 section 5).\n")
            .Append("# A blocked app still installs for the whole system, with an administrator's approval.\n");
        Append(text, AppsSection, defaults.BlockedApps);
        Append(text, CategoriesSection, defaults.BlockedCategories);
        Append(text, CapabilitiesSection, defaults.BlockedCapabilities);
        return text.ToString();
    }

    /// <summary>Reads the file. Never fails: an absent file blocks nothing but the host's defaults.</summary>
    /// <param name="content">File content, or <see langword="null"/> when the file is absent or unreadable.</param>
    /// <param name="defaults">The block list the host was built with, used when the file is absent.</param>
    public static AppPolicy Read(string? content, AppPackageDefaults? defaults = null)
    {
        if (content is null)
        {
            return Read(BuildDefaultContent(defaults ?? AppPackageDefaults.Standard));
        }

        Dictionary<string, bool> apps = new(StringComparer.Ordinal);
        Dictionary<string, bool> categories = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, bool> capabilities = new(StringComparer.Ordinal);
        List<string> warnings = [];
        foreach (ConfigLine line in ConfigDocumentLines.Parse(content))
        {
            if (line.Kind != ConfigLineKind.KeyValue || line.InMalformedSection || line.Key is not { Length: > 0 } key)
            {
                continue;
            }

            Dictionary<string, bool>? target = line.Group switch
            {
                AppsSection => apps,
                CategoriesSection => categories,
                CapabilitiesSection => capabilities,
                _ => null
            };
            if (target is null)
            {
                continue;
            }

            string value = (line.Value ?? string.Empty).Trim();
            bool isTrue = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            if (!isTrue && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"apps: {MachinePath.Value}:{line.LineNumber}: '{value}' is not true or false, line ignored");
                continue;
            }

            target[key] = isTrue;
        }

        return new AppPolicy(Blocked(apps, StringComparer.Ordinal), Blocked(categories, StringComparer.OrdinalIgnoreCase), Blocked(capabilities, StringComparer.Ordinal), warnings);
    }

    /// <summary>Blocks or unblocks one entry, keeping every other line exactly.</summary>
    /// <param name="content">Current file content, or <see langword="null"/> for a new file.</param>
    /// <param name="kind">Kind of rule.</param>
    /// <param name="value">App ID, category or capability.</param>
    /// <param name="blocked"><see langword="true"/> to block; <see langword="false"/> removes the line.</param>
    public static string Set(string? content, AppBlockKind kind, string value, bool blocked)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string key = value.Trim();
        if (key.Any(static c => char.IsWhiteSpace(c) || c is '=' or '[' or ']' or '#' or ';'))
        {
            throw new ArgumentException($"'{value}' cannot be a key of {FileName}.", nameof(value));
        }

        string edited = string.IsNullOrEmpty(content) ? DefaultContent : content;
        return blocked
            ? ConfigDocumentEditor.SetValue(edited, SectionOf(kind), key, "true")
            : ConfigDocumentEditor.RemoveValue(edited, SectionOf(kind), key);
    }

    private static void Append(StringBuilder text, string section, IReadOnlyList<string> values)
    {
        text.Append('\n').Append('[').Append(section).Append("]\n");
        foreach (string value in values)
        {
            text.Append(value).Append("=true\n");
        }
    }

    private static HashSet<string> Blocked(Dictionary<string, bool> entries, StringComparer comparer) =>
        entries.Where(static pair => pair.Value).Select(static pair => pair.Key).ToHashSet(comparer);
}

/// <summary>Kind of block-list rule (ADR 0189 §5).</summary>
public enum AppBlockKind
{
    /// <summary>One app, by its ID.</summary>
    App = 0,

    /// <summary>Every app of a category.</summary>
    Category = 1,

    /// <summary>Every app that asks for a capability.</summary>
    Capability = 2
}

/// <summary>Why the block list stops a personal install.</summary>
/// <param name="Kind">The matching rule's kind.</param>
/// <param name="Value">The matching app ID, category or capability.</param>
public sealed record AppBlockReason(AppBlockKind Kind, string Value)
{
    /// <inheritdoc />
    public override string ToString() => $"{AppPolicyConfigDocument.SectionOf(Kind)}: {Value}";
}

/// <summary>What <c>app-policy.config</c> blocks.</summary>
/// <param name="BlockedApps">Blocked app IDs.</param>
/// <param name="BlockedCategories">Blocked categories, compared without case.</param>
/// <param name="BlockedCapabilities">Blocked capabilities.</param>
/// <param name="Warnings">One <c>/var/log/syslog</c> line per ignored line.</param>
public sealed record AppPolicy(
    IReadOnlySet<string> BlockedApps,
    IReadOnlySet<string> BlockedCategories,
    IReadOnlySet<string> BlockedCapabilities,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Gets why <paramref name="manifest"/> cannot install for one user, or <see langword="null"/> when it can.</summary>
    public AppBlockReason? FindBlock(AppManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (BlockedApps.Contains(manifest.Id))
        {
            return new AppBlockReason(AppBlockKind.App, manifest.Id);
        }

        if (manifest.Presentation?.Category is { Length: > 0 } category && BlockedCategories.Contains(category))
        {
            return new AppBlockReason(AppBlockKind.Category, category);
        }

        string? capability = manifest.Capabilities.FirstOrDefault(BlockedCapabilities.Contains);
        return capability is null ? null : new AppBlockReason(AppBlockKind.Capability, capability);
    }
}
