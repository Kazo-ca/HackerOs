using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Processes;

/// <summary>
/// Pure rules of <c>/etc/security/limits.conf</c> and <c>/etc/security/limits.d/*.conf</c> (ADR 0125,
/// section 3), read at login like Linux's <c>pam_limits</c>: one rule per line,
/// <c>&lt;domain&gt; &lt;type&gt; &lt;item&gt; &lt;value&gt;</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Domain</b>: a login name, <c>@group</c>, or <c>*</c>.</item>
/// <item><b>Type</b>: <c>soft</c>, <c>hard</c>, or <c>-</c> (both).</item>
/// <item><b>Item</b>: <c>cpu</c> (minutes, as in <c>limits.conf(5)</c>), <c>rss</c> (KiB),
/// <c>nofile</c>, <c>nproc</c>. The other items of Linux are accepted and ignored: Kazo OS has no
/// such limit.</item>
/// <item><b>Value</b>: a non-negative integer, or <c>unlimited</c>, <c>infinity</c> or <c>-1</c>.</item>
/// </list>
/// <para>
/// Precedence follows <c>pam_limits</c>: a rule for the user beats a rule for one of its groups, which
/// beats a <c>*</c> rule, whatever the order; among rules of the same kind, the later one wins (files
/// are read <c>limits.conf</c> first, then <c>limits.d</c> in alphabetical order). A soft value above
/// the resulting hard one is lowered to it, as <c>setrlimit</c> would refuse it.
/// </para>
/// </remarks>
public static class ResourceLimitsConfiguration
{
    /// <summary>The main file, <c>0644</c>, owned by root.</summary>
    public static readonly VirtualPath LimitsFilePath = VirtualPath.Parse("/etc/security/limits.conf");

    /// <summary>The drop-in directory; its <c>*.conf</c> files are read in alphabetical order after the main file.</summary>
    public static readonly VirtualPath LimitsDirectoryPath = VirtualPath.Parse("/etc/security/limits.d");

    /// <summary>The <c>limits.conf</c> the OS ships: every line commented, like the distributions'.</summary>
    public const string DefaultLimitsFileContent =
        "# /etc/security/limits.conf\n" +
        "#\n" +
        "# Limits applied to each login session (ADR 0125), read like pam_limits when the session\n" +
        "# starts. Changes take effect at the next login. Drop-in files: /etc/security/limits.d/*.conf.\n" +
        "#\n" +
        "# Format: <domain> <type> <item> <value>\n" +
        "#   <domain>  a login name, @group, or * for everybody\n" +
        "#   <type>    soft (the value that applies), hard (its ceiling), or - for both\n" +
        "#   <item>    cpu    - max CPU time (MIN)\n" +
        "#             rss    - max resident set size (KB)\n" +
        "#             nofile - max number of open file descriptors\n" +
        "#             nproc  - max number of processes\n" +
        "#   <value>   a number, or unlimited\n" +
        "#\n" +
        "# Without any rule, a session gets the kernel's values (ulimit -a):\n" +
        "#   cpu unlimited, rss unlimited, nofile 1024 (hard 524288), nproc 4096.\n" +
        "#\n" +
        "#<domain>      <type>  <item>         <value>\n" +
        "#\n" +
        "#*               soft    nofile          4096\n" +
        "#@students       hard    nproc           200\n" +
        "#alice           -       cpu             60\n" +
        "\n" +
        "# End of file\n";

    /// <summary>The <c>limits.d</c> items Kazo OS knows but does not apply (Linux's other <c>RLIMIT_*</c>).</summary>
    private static readonly HashSet<string> IgnoredItems = new(StringComparer.Ordinal)
    {
        "core", "data", "fsize", "memlock", "stack", "as", "maxlogins", "maxsyslogins", "nonewprivs",
        "priority", "locks", "sigpending", "msgqueue", "nice", "rtprio", "chroot"
    };

    /// <summary>Parses one file into rules, in file order.</summary>
    /// <param name="content">The file's text.</param>
    /// <param name="fileName">Its path, for the warnings.</param>
    /// <param name="warnings">Receives one line per ignored rule, for <c>/var/log/auth.log</c>.</param>
    /// <returns>The valid rules.</returns>
    public static IReadOnlyList<ResourceLimitRule> Parse(string? content, string fileName, ICollection<string>? warnings = null)
    {
        List<ResourceLimitRule> rules = [];
        if (string.IsNullOrEmpty(content))
        {
            return rules;
        }

        string[] lines = content.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            int hash = line.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0)
            {
                line = line[..hash];
            }

            string[] fields = line.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0)
            {
                continue;
            }

            string where = $"{fileName}:{(index + 1).ToString(CultureInfo.InvariantCulture)}";
            if (fields.Length != 4)
            {
                warnings?.Add($"pam_limits: {where}: invalid line '{lines[index].Trim()}' - skipped");
                continue;
            }

            (string domain, string type, string item, string value) = (fields[0], fields[1], fields[2], fields[3]);
            if (type is not ("soft" or "hard" or "-"))
            {
                warnings?.Add($"pam_limits: {where}: unknown limit type '{type}'");
                continue;
            }

            if (IgnoredItems.Contains(item))
            {
                // Valid for Linux, meaningless here: silently accepted, as a kernel without the
                // feature would.
                continue;
            }

            ResourceLimitKind? kind = item switch
            {
                "cpu" => ResourceLimitKind.Cpu,
                "rss" => ResourceLimitKind.Rss,
                "nofile" => ResourceLimitKind.NoFile,
                "nproc" => ResourceLimitKind.NProc,
                _ => null
            };
            if (kind is null)
            {
                warnings?.Add($"pam_limits: {where}: unknown limit item '{item}'");
                continue;
            }

            if (!TryParseValue(value, kind.Value, out long amount))
            {
                warnings?.Add($"pam_limits: {where}: wrong limit value '{value}' for limit type '{type}'");
                continue;
            }

            rules.Add(new ResourceLimitRule(domain, type != "hard", type != "soft", kind.Value, amount));
        }

        return rules;
    }

    /// <summary>
    /// Applies <paramref name="rules"/> (in reading order) for one user over <paramref name="defaults"/>,
    /// with the precedence of <c>pam_limits</c>.
    /// </summary>
    /// <param name="rules">Every rule of every file, in reading order.</param>
    /// <param name="loginName">The user's login name.</param>
    /// <param name="groupNames">The names of the user's groups.</param>
    /// <param name="defaults">The kernel's limits the session starts from.</param>
    /// <param name="warnings">Receives one line per soft value lowered to its hard value.</param>
    /// <returns>The session's limits.</returns>
    public static ProcessResourceLimits Apply(
        IEnumerable<ResourceLimitRule> rules,
        string loginName,
        IReadOnlyCollection<string> groupNames,
        ProcessResourceLimits defaults,
        ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(defaults);
        groupNames ??= [];

        Dictionary<ResourceLimitKind, (long Value, int Priority)> soft = [];
        Dictionary<ResourceLimitKind, (long Value, int Priority)> hard = [];
        foreach (ResourceLimitRule rule in rules)
        {
            // pam_limits: the user's own line (0) beats a group line (1), which beats '*' (2).
            int priority = rule.Domain == loginName ? 0
                : rule.Domain.StartsWith('@') && groupNames.Contains(rule.Domain[1..], StringComparer.Ordinal) ? 1
                : rule.Domain == "*" ? 2
                : -1;
            if (priority < 0)
            {
                continue;
            }

            if (rule.Soft)
            {
                Set(soft, rule.Kind, rule.Value, priority);
            }

            if (rule.Hard)
            {
                Set(hard, rule.Kind, rule.Value, priority);
            }
        }

        ProcessResourceLimits limits = defaults;
        foreach (ResourceLimitKind kind in Enum.GetValues<ResourceLimitKind>())
        {
            ResourceLimit current = defaults[kind];
            long hardValue = hard.TryGetValue(kind, out (long Value, int Priority) h) ? h.Value : current.Hard;
            long softValue = soft.TryGetValue(kind, out (long Value, int Priority) s) ? s.Value : Math.Min(current.Soft, hardValue);
            if (softValue > hardValue)
            {
                warnings?.Add($"pam_limits: {loginName}: soft {Item(kind)} {ResourceLimit.Format(softValue)} above hard {ResourceLimit.Format(hardValue)}; lowered");
                softValue = hardValue;
            }

            limits = limits.With(kind, new ResourceLimit(softValue, hardValue));
        }

        return limits;
    }

    /// <summary>Gets the <c>limits.conf</c> item name of a limit.</summary>
    public static string Item(ResourceLimitKind kind) => kind switch
    {
        ResourceLimitKind.Cpu => "cpu",
        ResourceLimitKind.Rss => "rss",
        ResourceLimitKind.NoFile => "nofile",
        ResourceLimitKind.NProc => "nproc",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown resource limit.")
    };

    private static void Set(Dictionary<ResourceLimitKind, (long Value, int Priority)> target, ResourceLimitKind kind, long value, int priority)
    {
        if (!target.TryGetValue(kind, out (long Value, int Priority) existing) || priority <= existing.Priority)
        {
            target[kind] = (value, priority);
        }
    }

    /// <summary>Parses a value into the table's unit: seconds for <c>cpu</c> (given in minutes), bytes for <c>rss</c> (given in KiB).</summary>
    private static bool TryParseValue(string text, ResourceLimitKind kind, out long value)
    {
        if (text is "unlimited" or "infinity" or "-1")
        {
            value = ResourceLimit.Unlimited;
            return true;
        }

        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
        {
            value = 0;
            return false;
        }

        long factor = kind switch
        {
            ResourceLimitKind.Cpu => 60,
            ResourceLimitKind.Rss => 1024,
            _ => 1
        };
        value = parsed > (ResourceLimit.Unlimited - 1) / factor ? ResourceLimit.Unlimited : parsed * factor;
        return true;
    }
}

/// <summary>One valid line of <c>limits.conf</c>, value in the table's unit (seconds, bytes, counts).</summary>
/// <param name="Domain">A login name, <c>@group</c> or <c>*</c>.</param>
/// <param name="Soft">Whether it sets the soft value (<c>soft</c> or <c>-</c>).</param>
/// <param name="Hard">Whether it sets the hard value (<c>hard</c> or <c>-</c>).</param>
/// <param name="Kind">The limit.</param>
/// <param name="Value">The value; <see cref="ResourceLimit.Unlimited"/> for <c>unlimited</c>.</param>
public sealed record ResourceLimitRule(string Domain, bool Soft, bool Hard, ResourceLimitKind Kind, long Value);
