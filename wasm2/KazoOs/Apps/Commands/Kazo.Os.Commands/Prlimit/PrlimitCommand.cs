using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Processes;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Prlimit;

/// <summary>
/// Implements <c>prlimit</c> (util-linux, ADR 0125 section 4): prints or changes the resource limits
/// of a process through the public SDK (<see cref="HackProcess.GetResourceLimitsAsync"/>,
/// <see cref="HackProcess.SetResourceLimitAsync"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>prlimit --pid 42</c>: every limit, as a table.</item>
/// <item><c>prlimit --pid 42 --nofile</c>: that line only.</item>
/// <item><c>prlimit --pid 42 --nofile=512:1024</c>: soft and hard; <c>=512</c> sets both, <c>=512:</c>
/// the soft value only, <c>=:1024</c> the hard value only; <c>unlimited</c> is accepted.</item>
/// </list>
/// Without <c>--pid</c>, the process is <c>prlimit</c> itself, as in util-linux. Units are the
/// kernel's: seconds for <c>CPU</c>, bytes for <c>RSS</c>.
/// </remarks>
public sealed class PrlimitCommand : TerminalAppBase
{
    /// <summary>One resource: its option names, its column name and its unit.</summary>
    private sealed record Resource(ResourceLimitKind Kind, string LongOption, char ShortOption, string Name, string Unit);

    /// <summary>The resources, in the order util-linux prints them.</summary>
    private static readonly Resource[] Resources =
    [
        new(ResourceLimitKind.Cpu, "cpu", 't', "CPU", "seconds"),
        new(ResourceLimitKind.NoFile, "nofile", 'n', "NOFILE", "files"),
        new(ResourceLimitKind.NProc, "nproc", 'u', "NPROC", "processes"),
        new(ResourceLimitKind.Rss, "rss", 'm', "RSS", "bytes")
    ];

    /// <summary>Initializes the command with its manifest.</summary>
    public PrlimitCommand(AppManifest manifest) : base(manifest)
    {
    }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        long? pid = null;
        bool headings = true;
        List<(Resource Resource, string? Value)> requested = [];
        IReadOnlyList<string> args = context.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a is "-p" or "--pid")
            {
                if (i + 1 >= args.Count || !long.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
                {
                    await context.StandardError.WriteLineAsync(text["InvalidPid", i < args.Count ? args[i] : string.Empty]);
                    return 1;
                }

                pid = parsed;
                continue;
            }

            if (a.StartsWith("--pid=", StringComparison.Ordinal))
            {
                if (!long.TryParse(a["--pid=".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
                {
                    await context.StandardError.WriteLineAsync(text["InvalidPid", a["--pid=".Length..]]);
                    return 1;
                }

                pid = parsed;
                continue;
            }

            if (a == "--noheadings")
            {
                headings = false;
                continue;
            }

            if (a is "-h" or "--help")
            {
                await context.StandardOutput.WriteLineAsync(text["Usage"]);
                return 0;
            }

            if (ParseResource(a) is { } resource)
            {
                requested.Add(resource);
                continue;
            }

            await context.StandardError.WriteLineAsync(text["UnknownOption", a]);
            await context.StandardError.WriteLineAsync(text["Usage"]);
            return 1;
        }

        HackProcess process;
        HackResourceLimits limits;
        long target = pid ?? HackEnvironment.ProcessId;
        try
        {
            process = await HackProcess.GetProcessByIdAsync(target, cancellationToken);
            limits = await process.GetResourceLimitsAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or HackIOException or HackUnauthorizedAccessException)
        {
            await context.StandardError.WriteLineAsync(text["GetFailed", requested.Count == 1 ? requested[0].Resource.Name : "process", text["NoSuchProcess"]]);
            return 1;
        }

        List<Resource> shown = [];
        int status = 0;
        foreach ((Resource resource, string? value) in requested)
        {
            if (value is null)
            {
                shown.Add(resource);
                continue;
            }

            status |= await SetAsync(context, text, process, limits, resource, value, cancellationToken);
        }

        if (requested.Count == 0)
        {
            shown.AddRange(Resources);
        }

        if (shown.Count > 0)
        {
            await PrintAsync(context, text, limits, shown, headings);
        }

        return status;
    }

    /// <summary><c>--nofile</c>, <c>--nofile=…</c>, <c>-n</c>, <c>-n…</c>: the resource and its value, if any.</summary>
    private static (Resource Resource, string? Value)? ParseResource(string argument)
    {
        foreach (Resource resource in Resources)
        {
            string option = "--" + resource.LongOption;
            if (argument == option || argument == "-" + resource.ShortOption)
            {
                return (resource, null);
            }

            if (argument.StartsWith(option + "=", StringComparison.Ordinal))
            {
                return (resource, argument[(option.Length + 1)..]);
            }

            if (argument.Length > 2 && argument[0] == '-' && argument[1] == resource.ShortOption)
            {
                return (resource, argument[2..].TrimStart('='));
            }
        }

        return null;
    }

    /// <summary>Applies <c>soft:hard</c>, <c>value</c>, <c>soft:</c> or <c>:hard</c>.</summary>
    private static async Task<int> SetAsync(
        TerminalExecutionContext context, IStringLocalizer<Strings> text, HackProcess process, HackResourceLimits limits,
        Resource resource, string value, CancellationToken cancellationToken)
    {
        ResourceLimit current = limits[resource.Kind];
        int colon = value.IndexOf(':', StringComparison.Ordinal);
        string softText = colon < 0 ? value : value[..colon];
        string hardText = colon < 0 ? value : value[(colon + 1)..];
        long soft = current.Soft;
        long hard = current.Hard;
        if ((softText.Length > 0 && !TryParseLimit(softText, out soft)) || (hardText.Length > 0 && !TryParseLimit(hardText, out hard))
            || (softText.Length == 0 && hardText.Length == 0))
        {
            await context.StandardError.WriteLineAsync(text["InvalidLimit", resource.Name]);
            return 1;
        }

        try
        {
            await process.SetResourceLimitAsync(resource.Kind, soft, hard, cancellationToken);
            return 0;
        }
        catch (ArgumentException)
        {
            await context.StandardError.WriteLineAsync(text["SetFailed", resource.Name, text["InvalidArgument"]]);
        }
        catch (Exception exception) when (exception is HackUnauthorizedAccessException or NotSupportedException)
        {
            await context.StandardError.WriteLineAsync(text["SetFailed", resource.Name, text["OperationNotPermitted"]]);
        }
        catch (InvalidOperationException)
        {
            await context.StandardError.WriteLineAsync(text["SetFailed", resource.Name, text["NoSuchProcess"]]);
        }

        return 1;
    }

    private static bool TryParseLimit(string text, out long value)
    {
        if (text is "unlimited" or "infinity" or "-1")
        {
            value = ResourceLimit.Unlimited;
            return true;
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The table of util-linux: <c>RESOURCE DESCRIPTION SOFT HARD UNITS</c>.</summary>
    private static async Task PrintAsync(
        TerminalExecutionContext context, IStringLocalizer<Strings> text, HackResourceLimits limits, IReadOnlyList<Resource> resources, bool headings)
    {
        List<string[]> rows = [];
        if (headings)
        {
            rows.Add(["RESOURCE", "DESCRIPTION", "SOFT", "HARD", "UNITS"]);
        }

        foreach (Resource resource in resources)
        {
            ResourceLimit limit = limits[resource.Kind];
            rows.Add([resource.Name, text["Description." + resource.LongOption], ResourceLimit.Format(limit.Soft), ResourceLimit.Format(limit.Hard), resource.Unit]);
        }

        int[] widths = [.. Enumerable.Range(0, 5).Select(column => rows.Max(row => row[column].Length))];
        foreach (string[] row in rows)
        {
            // Names and descriptions to the left, numbers to the right, as util-linux aligns them.
            string line = $"{row[0].PadRight(widths[0])} {row[1].PadRight(widths[1])} {row[2].PadLeft(widths[2])} {row[3].PadLeft(widths[3])} {row[4]}";
            await context.StandardOutput.WriteLineAsync(line.TrimEnd());
        }
    }
}
