using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Jobs;

/// <summary>
/// <c>jobs [-l] [-p] [-r] [-s]</c> (ADR 0064): lists the background jobs of the calling shell,
/// like bash: <c>[1]+  Running   sleep 30 &amp;</c>; <c>-l</c> adds the process ids, <c>-p</c>
/// prints only them, <c>-r</c>/<c>-s</c> keep the running/stopped ones. Jobs that ended are
/// shown once, then forgotten.
/// </summary>
public sealed class JobsCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Shell is not { } shell)
        {
            await context.StandardError.WriteLineAsync(context.App.GetLocalizer<Strings>()["NoShell"]);
            return 1;
        }

        bool longFormat = false, pidsOnly = false, runningOnly = false, stoppedOnly = false;
        foreach (string argument in context.Arguments)
        {
            foreach (char flag in argument.TrimStart('-'))
            {
                switch (flag)
                {
                    case 'l': longFormat = true; break;
                    case 'p': pidsOnly = true; break;
                    case 'r': runningOnly = true; break;
                    case 's': stoppedOnly = true; break;
                    default:
                        await context.StandardError.WriteLineAsync(context.App.GetLocalizer<Strings>()["InvalidOption", flag]);
                        await context.StandardError.WriteLineAsync(context.App.GetLocalizer<Strings>()["Usage"]);
                        return 2;
                }
            }
        }

        IReadOnlyList<ShellJobInfo> jobs = shell.Jobs;
        int previous = jobs.Count >= 2 ? jobs[^2].Id : -1;
        foreach (ShellJobInfo job in jobs)
        {
            if ((runningOnly && job.State != ShellJobState.Running) || stoppedOnly)
            {
                continue;
            }

            if (pidsOnly)
            {
                foreach (int processId in job.ProcessIds.TakeLast(1))
                {
                    await context.StandardOutput.WriteLineAsync(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                continue;
            }

            char marker = job.IsCurrent ? '+' : job.Id == previous ? '-' : ' ';
            string state = job.State switch
            {
                ShellJobState.Running => "Running",
                ShellJobState.Terminated => "Terminated",
                _ when job.ExitStatus is null or 0 => "Done",
                _ => $"Exit {job.ExitStatus}"
            };
            string pid = longFormat && job.ProcessIds.Count > 0 ? $"{job.ProcessIds[^1],-6}" : string.Empty;
            string suffix = job.State == ShellJobState.Running ? " &" : string.Empty;
            await context.StandardOutput.WriteLineAsync($"[{job.Id}]{marker}  {pid}{state,-24}{job.Command}{suffix}");
        }

        return 0;
    }
}
