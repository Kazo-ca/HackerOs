using System.Globalization;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>
/// The <c>ulimit</c> built-in (ADR 0125, section 4): <c>getrlimit</c>/<c>setrlimit</c> of the shell's own
/// process, as bash does. A built-in because a separate process would only change its own limits;
/// the commands the shell starts afterwards inherit the new ones.
/// </summary>
public sealed partial class ShellInterpreter
{
    /// <summary>One resource <c>ulimit</c> knows: its option, its <c>-a</c> label and unit, and its kind.</summary>
    /// <param name="Option">The option letter.</param>
    /// <param name="Label">The label of <c>ulimit -a</c>, as bash prints it.</param>
    /// <param name="Unit">The unit of <c>ulimit -a</c>, empty for a count.</param>
    /// <param name="Kind">The limit; <see langword="null"/> for the file size, which Kazo OS does not limit.</param>
    /// <param name="Scale">How many table units one <c>ulimit</c> unit is (1024 bytes per KiB for <c>-m</c>).</param>
    private sealed record UlimitResource(char Option, string Label, string Unit, ResourceLimitKind? Kind, long Scale);

    /// <summary>The resources, in the order of <c>ulimit -a</c> (alphabetical by option, as bash).</summary>
    private static readonly UlimitResource[] UlimitResources =
    [
        new('f', "file size", "blocks", null, 1),
        new('m', "max memory size", "kbytes", ResourceLimitKind.Rss, 1024),
        new('n', "open files", string.Empty, ResourceLimitKind.NoFile, 1),
        new('t', "cpu time", "seconds", ResourceLimitKind.Cpu, 1),
        new('u', "max user processes", string.Empty, ResourceLimitKind.NProc, 1)
    ];

    /// <summary>Width of the label and unit columns of <c>ulimit -a</c>, as in bash.</summary>
    private const int UlimitLabelWidth = 40;

    /// <summary>
    /// <c>ulimit [-SH] [-a | -f | -m | -n | -t | -u] [limit]</c>: prints or changes one limit of the
    /// shell's process. Without <c>-S</c> or <c>-H</c>, prints the soft value and changes both.
    /// </summary>
    private async Task<int> RunUlimitAsync(Exec exec, IReadOnlyList<string> arguments)
    {
        ShellSession session = exec.Session;
        bool soft = false;
        bool hard = false;
        bool all = false;
        List<UlimitResource> selected = [];
        string? value = null;
        foreach (string argument in arguments)
        {
            if (value is null && argument.Length > 1 && argument[0] == '-' && !IsUlimitNumber(argument))
            {
                foreach (char option in argument.AsSpan(1))
                {
                    switch (option)
                    {
                        case 'S':
                            soft = true;
                            break;
                        case 'H':
                            hard = true;
                            break;
                        case 'a':
                            all = true;
                            break;
                        default:
                            UlimitResource? resource = Array.Find(UlimitResources, item => item.Option == option);
                            if (resource is null)
                            {
                                await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.UlimitInvalidOption", "-" + option]).ConfigureAwait(false);
                                await exec.Err.WriteLineAsync("ulimit: " + exec.Text["Shell.UlimitUsage"]).ConfigureAwait(false);
                                return 2;
                            }

                            selected.Add(resource);
                            break;
                    }
                }
            }
            else if (value is null)
            {
                value = argument;
            }
            else
            {
                await exec.Err.WriteLineAsync("ulimit: " + exec.Text["Shell.UlimitUsage"]).ConfigureAwait(false);
                return 2;
            }
        }

        if (all)
        {
            selected = [.. UlimitResources];
        }
        else if (selected.Count == 0)
        {
            selected.Add(UlimitResources[0]);
        }

        if (session.Host is null || session.ProcessId is not { } pid)
        {
            await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.UlimitNoProcess"]).ConfigureAwait(false);
            return 1;
        }

        using (HackExecutionContext.Enter(session.Host, session.CurrentDirectory))
        {
            HackProcess process;
            HackResourceLimits limits;
            try
            {
                process = await HackProcess.GetProcessByIdAsync(pid.Value, exec.Token).ConfigureAwait(false);
                limits = await process.GetResourceLimitsAsync(exec.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or HackIOException or HackUnauthorizedAccessException)
            {
                await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.UlimitNoProcess"]).ConfigureAwait(false);
                return 1;
            }

            if (value is null || all)
            {
                foreach (UlimitResource resource in selected)
                {
                    string shown = FormatUlimit(resource, limits, hard && !soft);
                    await exec.Out.WriteLineAsync(selected.Count == 1 ? shown : UlimitRow(resource) + " " + shown).ConfigureAwait(false);
                }

                return 0;
            }

            int status = 0;
            foreach (UlimitResource resource in selected)
            {
                status = Math.Max(status, await SetUlimitAsync(exec, process, resource, limits, value, soft, hard).ConfigureAwait(false));
            }

            return status;
        }
    }

    /// <summary>Changes one limit; bash's messages for an invalid value or a refused change.</summary>
    private static async Task<int> SetUlimitAsync(
        Exec exec, HackProcess process, UlimitResource resource, HackResourceLimits limits, string text, bool soft, bool hard)
    {
        long requested;
        if (text is "unlimited" or "infinity")
        {
            requested = ResourceLimit.Unlimited;
        }
        else if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
        {
            requested = parsed > ResourceLimit.Unlimited / resource.Scale ? ResourceLimit.Unlimited : parsed * resource.Scale;
        }
        else if (resource.Kind is { } current && text is "soft" or "hard")
        {
            // bash: the current soft or hard value.
            requested = text == "soft" ? limits[current].Soft : limits[current].Hard;
        }
        else
        {
            await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.UlimitInvalidNumber", text]).ConfigureAwait(false);
            return 1;
        }

        if (resource.Kind is not { } kind)
        {
            // Kazo OS has no file size limit: it is unlimited and stays so (ADR 0125, section 4).
            if (requested == ResourceLimit.Unlimited)
            {
                return 0;
            }

            await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.UlimitCannotModify", resource.Label, exec.Text["Errno.OperationNotPermitted"]]).ConfigureAwait(false);
            return 1;
        }

        // Neither -S nor -H: both, as bash.
        bool both = soft == hard;
        ResourceLimit before = limits[kind];
        ResourceLimit after = new(both || soft ? requested : before.Soft, both || hard ? requested : before.Hard);
        if (!both && hard && after.Soft > after.Hard)
        {
            // Lowering the hard value below the soft one lowers the soft value too, as setrlimit expects.
            after = after with { Soft = after.Hard };
        }

        try
        {
            await process.SetResourceLimitAsync(kind, after.Soft, after.Hard, exec.Token).ConfigureAwait(false);
            return 0;
        }
        catch (ArgumentException)
        {
            await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.UlimitCannotModify", resource.Label, exec.Text["Errno.InvalidArgument"]]).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HackUnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.UlimitCannotModify", resource.Label, exec.Text["Errno.OperationNotPermitted"]]).ConfigureAwait(false);
        }

        return 1;
    }

    /// <summary>The value of one resource in <c>ulimit</c>'s unit.</summary>
    private static string FormatUlimit(UlimitResource resource, HackResourceLimits limits, bool hard)
    {
        if (resource.Kind is not { } kind)
        {
            return "unlimited";
        }

        long value = hard ? limits[kind].Hard : limits[kind].Soft;
        return value == ResourceLimit.Unlimited
            ? "unlimited"
            : (value / resource.Scale).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The label of <c>ulimit -a</c>: <c>open files                      (-n)</c>.</summary>
    private static string UlimitRow(UlimitResource resource)
    {
        string option = resource.Unit.Length == 0 ? $"(-{resource.Option})" : $"({resource.Unit}, -{resource.Option})";
        return resource.Label.PadRight(UlimitLabelWidth - option.Length) + option;
    }

    /// <summary>A negative number is a value (<c>-1</c>), not an option.</summary>
    private static bool IsUlimitNumber(string argument) => argument.Length > 1 && argument[0] == '-' && argument.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;
}
