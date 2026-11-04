using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Kill;

/// <summary>
/// Implements the <c>kill</c> terminal command (`P4-W5-CMD-004`, job specs and signal names since
/// ADR 0064). Sends a signal to processes by PID or to the calling shell's jobs (<c>%1</c>,
/// <c>%%</c>, <c>%-</c>, <c>%name</c>). <c>SIGKILL</c> (<c>-9</c>, <c>-KILL</c>, <c>-s KILL</c>)
/// kills at once; every other signal (<c>TERM</c> by default, <c>INT</c>, <c>HUP</c>,
/// <c>QUIT</c>…) asks the process to stop and forces it after 5 seconds, which is what an app
/// that does not handle signals does. <c>kill -l</c> lists the signals.
/// </summary>
/// <remarks>
/// ADR 0069: the polite path raises the target's <c>StopRequested</c> with
/// <see cref="ProcessExitReason.Terminated"/> and a deadline 5 simulated seconds away, then cancels
/// its token; <c>SIGKILL</c> raises nothing, and the target's leftover continuations are dropped.
/// </remarks>
public sealed class KillCommand : TerminalAppBase
{
    private static readonly string[] SignalNames =
    [
        "HUP", "INT", "QUIT", "ILL", "TRAP", "ABRT", "BUS", "FPE", "KILL", "USR1", "SEGV", "USR2", "PIPE", "ALRM", "TERM",
        "STKFLT", "CHLD", "CONT", "STOP", "TSTP", "TTIN", "TTOU", "URG", "XCPU", "XFSZ", "VTALRM", "PROF", "WINCH", "IO", "PWR", "SYS"
    ];

    /// <summary>Initializes the command with its manifest.</summary>
    public KillCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        int signal = 15;
        List<string> targets = [];
        IReadOnlyList<string> args = context.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a is "-l" or "-L" or "--list")
            {
                await ListSignalsAsync(context, args.Skip(i + 1).FirstOrDefault());
                return 0;
            }

            if (a is "-s" or "-n" && i + 1 < args.Count)
            {
                if (ParseSignal(args[++i]) is not { } named)
                {
                    context.StandardError.WriteLine(context.App.GetLocalizer<Strings>()["InvalidSignal", args[i]]);
                    return 1;
                }

                signal = named;
                continue;
            }

            if (a.Length > 1 && a[0] == '-' && targets.Count == 0)
            {
                // -9, -KILL, -SIGKILL: the signal comes before the targets.
                if (ParseSignal(a[1..]) is not { } parsed)
                {
                    context.StandardError.WriteLine(context.App.GetLocalizer<Strings>()["InvalidSignal", a[1..]]);
                    return 1;
                }

                signal = parsed;
                continue;
            }

            targets.Add(a);
        }

        if (targets.Count == 0)
        {
            context.StandardError.WriteLine(context.App.GetLocalizer<Strings>()["Usage"]);
            return 1;
        }

        bool forceKill = signal == 9;
        int status = 0;
        foreach (string target in targets)
        {
            if (target.StartsWith('%'))
            {
                status |= KillJob(context, target, forceKill);
                continue;
            }

            if (!long.TryParse(target, out long pidVal))
            {
                context.StandardError.WriteLine(context.App.GetLocalizer<Strings>()["InvalidTarget", target]);
                status = 1;
                continue;
            }

            if (signal == 0)
            {
                // kill -0 only checks that the process exists.
                bool exists = context.App.Processes.ListProcesses().Any(p => p.Pid.Value == pidVal);
                status |= exists ? 0 : 1;
                continue;
            }

            var pid = ProcessId.FromInt64(pidVal);
            try
            {
                if (forceKill)
                {
                    context.App.Processes.Kill(pid);
                }
                else
                {
                    // SIGTERM (ADR 0069): StopRequested with the reason Terminated, the token, then SIGKILL after 5 s.
                    await context.App.Processes.StopAsync(pid, TimeSpan.FromSeconds(5), ProcessExitReason.Terminated, cancellationToken);
                }
            }
            catch (KeyNotFoundException)
            {
                context.StandardError.WriteLine(context.App.GetLocalizer<Strings>()["NoSuchProcess", pidVal]);
                status = 1;
            }
            catch (AppGatewayAccessDeniedException exception)
            {
                context.StandardError.WriteLine($"kill: ({pidVal}) - {exception.Message}");
                status = 1;
            }
        }

        return status;
    }

    /// <summary><c>kill %n</c>: the calling shell stops the job (its processes are its children).</summary>
    private static int KillJob(TerminalExecutionContext context, string spec, bool force)
    {
        if (context.Shell is not { } shell)
        {
            context.StandardError.WriteLine(context.App.GetLocalizer<Strings>()["NoShell", spec]);
            return 1;
        }

        IReadOnlyList<ShellJobInfo> jobs = shell.Jobs;
        string body = spec[1..];
        ShellJobInfo? job = body switch
        {
            "" or "%" or "+" => jobs.LastOrDefault(),
            "-" => jobs.Count >= 2 ? jobs[^2] : null,
            _ when int.TryParse(body, out int id) => jobs.FirstOrDefault(j => j.Id == id),
            _ => jobs.LastOrDefault(j => j.Command.StartsWith(body, StringComparison.Ordinal))
        };
        if (job is null || !shell.KillJob(job.Id, force))
        {
            context.StandardError.WriteLine(context.App.GetLocalizer<Strings>()["NoSuchJob", spec]);
            return 1;
        }

        return 0;
    }

    private static int? ParseSignal(string text)
    {
        if (int.TryParse(text, out int number))
        {
            return number is >= 0 and <= 64 ? number : null;
        }

        string name = text.ToUpperInvariant();
        name = name.StartsWith("SIG", StringComparison.Ordinal) ? name[3..] : name;
        int index = Array.IndexOf(SignalNames, name);
        return index < 0 ? null : index + 1;
    }

    private static async Task ListSignalsAsync(TerminalExecutionContext context, string? only)
    {
        if (only is not null)
        {
            await context.StandardOutput.WriteLineAsync(int.TryParse(only, out int number) && number is >= 1 and <= 31
                ? SignalNames[number - 1]
                : ParseSignal(only)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?");
            return;
        }

        for (int i = 0; i < SignalNames.Length; i++)
        {
            await context.StandardOutput.WriteAsync($"{i + 1,2}) SIG{SignalNames[i],-8}");
            if ((i + 1) % 5 == 0 || i == SignalNames.Length - 1)
            {
                await context.StandardOutput.WriteLineAsync();
            }
        }
    }
}
