using System.Text.Json;
using Kazo.Os.AppSdk.Threading;
using Kazo.Os.Platform.WorkerHost.Protocol;

namespace Kazo.Os.Platform.WorkerHost.Worker;

/// <summary>
/// <see cref="IAppBackgroundGateway"/> of a process that already runs in a worker (ADR 0128, section 6):
/// the request goes to the OS as a <c>bg.run</c> call, which places the task like any other (the only
/// child a process in a worker may start, <c>StartChild</c> staying refused, ADR 0124 section 5). The
/// task's reports come back as <c>bgp</c> messages numbered by this table.
/// </summary>
internal sealed class RemoteBackgroundGateway(RemoteProcessChannel channel) : IAppBackgroundGateway
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Action<HackBackgroundProgress>> _progress = [];
    private int _nextJob;

    /// <inheritdoc />
    public async Task<string?> RunAsync(HackBackgroundJobRequest request, Action<HackBackgroundProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        int job;
        lock (_gate)
        {
            job = ++_nextJob;
            if (progress is not null)
            {
                _progress[job] = progress;
            }
        }

        try
        {
            JsonElement value = await channel.CallAsync(RemoteOps.BackgroundRun, writer =>
            {
                writer.WriteNumber("j", job);
                writer.WriteBoolean("pg", progress is not null);
                writer.WriteStartObject("jt");
                writer.WriteString("asm", request.Target.AssemblyName);
                writer.WriteString("ty", request.Target.TypeName);
                if (request.Target.MethodName is { } method)
                {
                    writer.WriteString("me", method);
                }

                writer.WriteString("it", request.Target.InputTypeName);
                writer.WriteEndObject();
                writer.WriteString("ji", request.InputJson);
                if (request.Name is { } name)
                {
                    writer.WriteString("n", name);
                }

                writer.WriteNumber("ni", request.Nice);
            }, cancellationToken).ConfigureAwait(true);
            return value.GetOptionalString("r");
        }
        finally
        {
            lock (_gate)
            {
                _progress.Remove(job);
            }
        }
    }

    /// <summary>Delivers one <c>bgp</c> report to the task's caller, in this process's thread.</summary>
    /// <param name="job">The number the call carried.</param>
    /// <param name="progress">The report.</param>
    public void Deliver(int job, HackBackgroundProgress progress)
    {
        Action<HackBackgroundProgress>? target;
        lock (_gate)
        {
            _progress.TryGetValue(job, out target);
        }

        target?.Invoke(progress);
    }
}
