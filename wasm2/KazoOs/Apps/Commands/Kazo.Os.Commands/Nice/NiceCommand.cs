using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Simulation.Abstractions.Processes;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Nice;

/// <summary>
/// Implements <c>nice [-n N] [command [args...]]</c> (ADR 0127), GNU coreutils style. Without a
/// command it prints its own niceness (inherited from the shell). With one, it asks its shell
/// (<see cref="IShellSession.RunNicedAsync"/>) to start the command as its child at its niceness
/// plus <c>N</c> (<c>10</c> by default; <c>-N</c> and <c>--adjustment=N</c> work too). Lowering it
/// needs root: without it, the command runs anyway after a warning, as GNU <c>nice</c> does.
/// </summary>
/// <remarks>
/// Niceness only orders the queue of the worker pool (ADR 0071): it says which waiting command
/// gets the next free virtual core. A command on the main thread runs exactly as before.
/// </remarks>
public sealed class NiceCommand : TerminalAppBase
{
    /// <summary>GNU <c>nice</c>'s own failure code.</summary>
    private const int NiceFailed = 125;

    /// <summary>Initializes the command with its manifest.</summary>
    public NiceCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();

        int adjustment = ProcessNice.DefaultAdjustment;
        IReadOnlyList<string> args = context.Arguments;
        int index = 0;
        for (; index < args.Count; index++)
        {
            string arg = args[index];
            string? value = null;
            if (arg is "-n" or "--adjustment")
            {
                if (index + 1 >= args.Count)
                {
                    await context.StandardError.WriteLineAsync(text["MissingArgument", arg]);
                    return NiceFailed;
                }

                value = args[++index];
            }
            else if (arg.StartsWith("--adjustment=", StringComparison.Ordinal))
            {
                value = arg["--adjustment=".Length..];
            }
            else if (arg.StartsWith("-n", StringComparison.Ordinal) && arg.Length > 2)
            {
                value = arg[2..];
            }
            else if (arg.Length > 1 && arg[0] == '-' && (char.IsAsciiDigit(arg[1]) || (arg.Length > 2 && arg[1] == '-' && char.IsAsciiDigit(arg[2]))))
            {
                // The historical form: `nice -5 cmd` is +5, `nice --5 cmd` is -5.
                value = arg[1..];
            }
            else if (arg == "--")
            {
                index++;
                break;
            }
            else if (arg.StartsWith('-') && arg.Length > 1)
            {
                await context.StandardError.WriteLineAsync(text["InvalidOption", arg.TrimStart('-')]);
                await context.StandardError.WriteLineAsync(text["Usage"]);
                return NiceFailed;
            }
            else
            {
                break;
            }

            if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out adjustment))
            {
                await context.StandardError.WriteLineAsync(text["InvalidAdjustment", value]);
                return NiceFailed;
            }
        }

        if (index >= args.Count)
        {
            // `nice` alone prints the niceness it inherited, one number per line.
            int own = context.App.Processes.GetNice(context.App.ProcessId);
            await context.StandardOutput.WriteLineAsync(own.ToString(CultureInfo.InvariantCulture));
            return 0;
        }

        if (context.Shell is not { } shell)
        {
            await context.StandardError.WriteLineAsync(text["NoShell"]);
            return NiceFailed;
        }

        // The kernel clamps, and so does GNU nice: `nice -n 100` is 19.
        adjustment = Math.Clamp(adjustment, ProcessNice.Min - ProcessNice.Max, ProcessNice.Max - ProcessNice.Min);
        return await shell.RunNicedAsync([.. args.Skip(index)], adjustment, context.StandardOutput, context.StandardError, cancellationToken);
    }
}
