using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Renice;

/// <summary>
/// Implements <c>renice [-n] N [-p] pid...</c> (ADR 0127), util-linux style: sets the niceness of
/// running processes and prints <c>1234 (process ID) old priority 0, new priority 5</c> for each.
/// The rules are Linux's, checked by the process gateway: a process of the same user may be made
/// nicer; lowering a niceness or touching another user's process needs root (<c>sudo renice</c>).
/// Only process IDs are supported (<c>-g</c> and <c>-u</c> are refused).
/// </summary>
public sealed class ReniceCommand : TerminalAppBase
{
    /// <summary>Initializes the command with its manifest.</summary>
    public ReniceCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();

        int? nice = null;
        List<string> targets = [];
        IReadOnlyList<string> args = context.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg is "-n" or "--priority")
            {
                if (i + 1 >= args.Count || !TryParseNice(args[++i], out int value))
                {
                    await context.StandardError.WriteLineAsync(text["InvalidPriority", i < args.Count ? args[i] : string.Empty]);
                    return 1;
                }

                nice = value;
                continue;
            }

            if (arg is "-p" or "--pid")
            {
                continue;
            }

            if (arg is "-g" or "--pgrp" or "-u" or "--user")
            {
                await context.StandardError.WriteLineAsync(text["OnlyProcessIds", arg]);
                return 1;
            }

            if (nice is null && TryParseNice(arg, out int positional))
            {
                // The historical form: `renice 5 1234`.
                nice = positional;
                continue;
            }

            targets.Add(arg);
        }

        if (nice is not { } requested || targets.Count == 0)
        {
            await context.StandardError.WriteLineAsync(text["Usage"]);
            return 1;
        }

        int status = 0;
        foreach (string target in targets)
        {
            if (!long.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out long value) || value <= 0)
            {
                await context.StandardError.WriteLineAsync(text["InvalidPid", target]);
                status = 1;
                continue;
            }

            ProcessId pid = ProcessId.FromInt64(value);
            try
            {
                int old = context.App.Processes.GetNice(pid);
                int now = context.App.Processes.SetNice(pid, requested);
                await context.StandardOutput.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture, $"{value} (process ID) old priority {old}, new priority {now}"));
            }
            catch (KeyNotFoundException)
            {
                await context.StandardError.WriteLineAsync(text["NoSuchProcess", value]);
                status = 1;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or AppGatewayAccessDeniedException)
            {
                string reason = exception is UnauthorizedAccessException ? exception.Message : text["PermissionDenied"];
                await context.StandardError.WriteLineAsync(text["Failed", value, reason]);
                status = 1;
            }
        }

        return status;
    }

    private static bool TryParseNice(string text, out int nice) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out nice);
}
