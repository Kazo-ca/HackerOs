using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Watch;

/// <summary>
/// <c>watch [-n SECONDS] [-t] [-g] [-e] [-d] COMMAND...</c> (ADR 0064), procps style: runs the
/// command line every interval (2 s by default, 0.1 s minimum) through the calling shell
/// (<see cref="IShellSession.RunAsync"/>, a subshell: pipes, variables and redirections work)
/// and shows its latest output full screen under an <c>Every 2.0s: …</c> header. <c>q</c> or
/// Ctrl+C quits; <c>-g</c> quits when the output changes, <c>-e</c> when the command fails.
/// Without a terminal it prints each run one after the other.
/// </summary>
public sealed class WatchCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IStringLocalizer<Strings> messages = context.App.GetLocalizer<Strings>();
        if (context.Shell is not { } shell)
        {
            await context.StandardError.WriteLineAsync(messages["NoShell"]);
            return 1;
        }

        double interval = 2;
        bool noTitle = false, untilChange = false, untilError = false;
        int index = 0;
        IReadOnlyList<string> args = context.Arguments;
        for (; index < args.Count; index++)
        {
            string arg = args[index];
            if (arg is "-n" or "--interval" && index + 1 < args.Count)
            {
                if (!double.TryParse(args[++index], NumberStyles.Float, CultureInfo.InvariantCulture, out interval))
                {
                    await context.StandardError.WriteLineAsync(messages["BadArgument", args[index]]);
                    return 1;
                }

                continue;
            }

            if (arg is "-t" or "--no-title") { noTitle = true; continue; }
            if (arg is "-g" or "--chgexit") { untilChange = true; continue; }
            if (arg is "-e" or "--errexit") { untilError = true; continue; }
            if (arg is "-d" or "--differences" or "-c" or "--color" or "-x" or "--exec" or "-p" or "--precise") { continue; }
            if (arg == "--")
            {
                index++;
            }

            break;
        }

        if (index >= args.Count)
        {
            await context.StandardError.WriteLineAsync(messages["NoCommand"]);
            await context.StandardError.WriteLineAsync(messages["Usage"]);
            return 1;
        }

        interval = Math.Max(0.1, interval);
        string commandLine = string.Join(' ', args.Skip(index));
        string? previous = null;
        IFullScreenTerminalSession? screen = context.FullScreen;
        if (screen is not null)
        {
            await screen.EnterAlternateScreenAsync(cancellationToken);
        }

        try
        {
            while (true)
            {
                StringWriter output = new() { NewLine = "\n" };
                int status = await shell.RunAsync(commandLine, TextReader.Null, output, output, cancellationToken);
                string text = output.ToString();
                if (screen is null)
                {
                    await context.StandardOutput.WriteAsync(text);
                }
                else
                {
                    string header = messages["Header", interval, commandLine];
                    string clock = $"{KazoEnvironment.MachineName}: {context.App.Clock.UtcNow:ddd MMM d HH:mm:ss yyyy}";
                    int columns = screen.Viewport.Columns;
                    List<string> lines = noTitle
                        ? []
                        : [header + new string(' ', Math.Max(1, columns - header.Length - clock.Length)) + clock, string.Empty];
                    lines.AddRange(text.TrimEnd('\n').Split('\n'));
                    await screen.RenderAsync(new TerminalScreenFrame(lines, new TerminalCursor(0, 0, Visible: false), "watch"), cancellationToken);
                }

                if ((untilChange && previous is not null && previous != text) || (untilError && status != 0))
                {
                    return untilError && status != 0 ? 8 : 0;
                }

                previous = text;
                if (await WaitAsync(screen, TimeSpan.FromSeconds(interval), cancellationToken))
                {
                    return 0;
                }
            }
        }
        finally
        {
            if (screen is not null)
            {
                await screen.LeaveAlternateScreenAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>Waits for the interval; true when <c>q</c> (or Ctrl+C) asked to quit.</summary>
    private static async Task<bool> WaitAsync(IFullScreenTerminalSession? screen, TimeSpan interval, CancellationToken cancellationToken)
    {
        if (screen is null)
        {
            await Task.Delay(interval, cancellationToken);
            return false;
        }

        using CancellationTokenSource tick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        tick.CancelAfter(interval);
        while (true)
        {
            TerminalKeyEvent key;
            try
            {
                key = await screen.ReadKeyAsync(tick.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (key.Key == TerminalKey.Character && (key.Text is "q" or "Q" || (key.Control && key.Text is "c" or "C")))
            {
                return true;
            }

            if (key.Key == TerminalKey.Resize)
            {
                return false;
            }
        }
    }
}
