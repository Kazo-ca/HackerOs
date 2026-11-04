using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Ping;

/// <summary>
/// Simulated <c>ping</c> terminal command (P4-W4-006): four echo requests through
/// <see cref="KazoPing"/>, then the usual statistics block. Resolution and the real-network
/// fallback (ADR 0028) belong to the OS (ADR 0056, section 6): a host outside the simulated
/// network is reached through the optional server only when the OS can, otherwise the name simply
/// does not resolve.
/// </summary>
public sealed class PingCommand : TerminalAppBase
{
    private const int EchoCount = 4;

    /// <summary>Initializes the command with its manifest.</summary>
    public PingCommand(AppManifest manifest) : base(manifest)
    {
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();

        if (context.Arguments.Count == 0)
        {
            context.StandardError.WriteLine(text["Usage"]);
            return 1;
        }

        string target = context.Arguments[0];
        List<KazoPingReply> replies = [];
        for (int sequence = 1; sequence <= EchoCount; sequence++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            KazoPingReply reply;
            try
            {
                reply = await KazoPing.SendAsync(target, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (KazoPingException)
            {
                context.StandardOutput.WriteLine(text["CannotResolve", target]);
                return 2;
            }

            if (sequence == 1)
            {
                context.StandardOutput.WriteLine(reply.Address is null
                    ? text["Header", target]
                    : text["HeaderAddress", target, reply.Address]);
            }

            replies.Add(reply);
            string from = reply.Address?.ToString() ?? target;
            switch (reply.Status)
            {
                case KazoIPStatus.Success:
                    context.StandardOutput.WriteLine(
                        text["Reply", from, sequence, reply.Ttl, FormatMilliseconds(reply.RoundtripTime)]);
                    break;
                case KazoIPStatus.DestinationHostUnreachable:
                    context.StandardOutput.WriteLine(text["Unreachable", from, sequence]);
                    break;
                default:
                    context.StandardOutput.WriteLine(text["Timeout", sequence]);
                    break;
            }
        }

        return WriteStatistics(context, text, target, replies);
    }

    /// <summary>Writes the summary <c>ping</c> prints on exit and returns its status: 0 if any reply came back.</summary>
    private static int WriteStatistics(TerminalExecutionContext context, IStringLocalizer<Strings> text, string target, IReadOnlyList<KazoPingReply> replies)
    {
        double[] times = [.. replies.Where(r => r.Status == KazoIPStatus.Success).Select(r => r.RoundtripTime.TotalMilliseconds)];
        int lossPercent = replies.Count == 0 ? 100 : (int)Math.Round(100.0 * (replies.Count - times.Length) / replies.Count);

        context.StandardOutput.WriteLine();
        context.StandardOutput.WriteLine(text["Statistics.Title", target]);
        context.StandardOutput.WriteLine(
            text["Statistics.Summary", replies.Count, times.Length, lossPercent, Math.Max(0, replies.Count - 1) * 1001]);
        if (times.Length == 0)
        {
            return 1;
        }

        double average = times.Average();
        double deviation = Math.Sqrt(times.Average(t => (t - average) * (t - average)));
        // The rtt line keeps its machine format in every language, as iputils prints it.
        context.StandardOutput.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"rtt min/avg/max/mdev = {times.Min():0.000}/{average:0.000}/{times.Max():0.000}/{deviation:0.000} ms"));
        return 0;
    }

    private static string FormatMilliseconds(TimeSpan time) =>
        Math.Round(time.TotalMilliseconds, 3).ToString(CultureInfo.InvariantCulture);
}
