using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.Settings;

namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>
/// <c>/etc/hackeros/app-sources.config</c> (ADR 0182 §3): the hosts the OS itself contacts to
/// download app packages, and the GitHub owners whose apps install without a warning.
/// </summary>
/// <remarks>
/// <code>
/// [hosts]
/// github=raw.githubusercontent.com
/// [trusted]
/// github.com/Kazo-ca=true
/// </code>
/// Read line by line without a schema, like <see cref="AppsConfigDocument"/>: a bad line is skipped
/// with a warning, the last occurrence of a key wins.
/// </remarks>
public static class AppSourcesConfigDocument
{
    /// <summary>Section naming the download host of each source kind.</summary>
    public const string HostsSection = "hosts";

    /// <summary>Section listing the trusted owners, as <c>github.com/&lt;owner&gt;=true</c>.</summary>
    public const string TrustedSection = "trusted";

    /// <summary>Key of <see cref="HostsSection"/> for GitHub sources.</summary>
    public const string GitHubHostKey = "github";

    /// <summary>Download host used when the file is absent, or names no GitHub host.</summary>
    public const string DefaultGitHubHost = "raw.githubusercontent.com";

    /// <summary>Owner trusted by default (ADR 0182, answer A to Q3).</summary>
    public const string DefaultTrustedKey = "github.com/Kazo-ca";

    /// <summary>File name under <c>/etc/hackeros</c>.</summary>
    public const string FileName = "app-sources.config";

    /// <summary>Machine file, <c>system:system 0644</c>.</summary>
    public static VirtualPath MachinePath { get; } = VirtualPath.Parse($"/etc/hackeros/{FileName}");

    /// <summary>Content written when the file does not exist yet.</summary>
    public const string DefaultContent =
        "# Where the OS downloads app packages, and whose apps it trusts (ADR 0182).\n"
        + "# hackpkg contacts only the hosts below, itself, never through an app.\n"
        + "[hosts]\n"
        + "github=" + DefaultGitHubHost + "\n"
        + "\n"
        + "# GitHub owners whose apps install without a warning. Others need\n"
        + "# 'hackpkg install --allow-untrusted' and an administrator's confirmation.\n"
        + "[trusted]\n"
        + DefaultTrustedKey + "=true\n";

    /// <summary>Reads the file. Never fails: an absent file gives the defaults.</summary>
    /// <param name="content">File content, or <see langword="null"/> when the file is absent or unreadable.</param>
    public static AppSourcesConfig Read(string? content)
    {
        if (content is null)
        {
            return Read(DefaultContent);
        }

        Dictionary<string, string> hosts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, (bool Trusted, int Line)> trusted = new(StringComparer.OrdinalIgnoreCase);
        List<string> warnings = [];
        foreach (ConfigLine line in ConfigDocumentLines.Parse(content))
        {
            if (line.Kind != ConfigLineKind.KeyValue || line.InMalformedSection || line.Key is not { Length: > 0 } key)
            {
                continue;
            }

            string value = (line.Value ?? string.Empty).Trim();
            if (string.Equals(line.Group, HostsSection, StringComparison.Ordinal))
            {
                if (IsHostName(value))
                {
                    hosts[key] = value.ToLowerInvariant();
                }
                else
                {
                    warnings.Add($"apps: {MachinePath.Value}:{line.LineNumber}: '{value}' is not a host name, line ignored");
                }
            }
            else if (string.Equals(line.Group, TrustedSection, StringComparison.Ordinal))
            {
                bool isTrue = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                if (!isTrue && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"apps: {MachinePath.Value}:{line.LineNumber}: '{value}' is not true or false, line ignored");
                    continue;
                }

                trusted[key] = (isTrue, line.LineNumber);
            }
        }

        return new AppSourcesConfig(
            hosts.TryGetValue(GitHubHostKey, out string? gitHub) ? gitHub : DefaultGitHubHost,
            trusted.Where(static pair => pair.Value.Trusted).Select(static pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase),
            warnings);
    }

    private static bool IsHostName(string value) =>
        value.Length is > 0 and <= 253
        && Uri.CheckHostName(value) == UriHostNameType.Dns
        && value.Contains('.', StringComparison.Ordinal);
}

/// <summary>What <c>app-sources.config</c> allows.</summary>
/// <param name="GitHubHost">Host the OS downloads GitHub packages from.</param>
/// <param name="TrustedKeys">Trusted <c>github.com/&lt;owner&gt;</c> keys, compared without case.</param>
/// <param name="Warnings">One <c>/var/log/syslog</c> line per ignored line.</param>
public sealed record AppSourcesConfig(string GitHubHost, IReadOnlySet<string> TrustedKeys, IReadOnlyList<string> Warnings)
{
    /// <summary>Gets whether apps of <paramref name="source"/>'s owner install without a warning.</summary>
    public bool IsTrusted(AppPackageSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return TrustedKeys.Contains(source.TrustKey);
    }
}
