using System.Text.Json;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.WorkerHost.Protocol;

namespace Kazo.Os.Platform.WorkerHost.Host;

/// <summary>
/// The OS side of a window rendered in a worker (trial of ADR 0129, verdict in ADR 0133): sends the
/// window's inputs (<c>uin</c>), knows its worker, and closes it. Its outputs reach the receiver given
/// to <see cref="WorkerRemoteProcessHost.StartWindowAsync"/>.
/// </summary>
public sealed class RemoteWindowProcess
{
    /// <summary>How long a closed window may take to end before its worker is terminated.</summary>
    public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private readonly WorkerPool _pool;
    private readonly WorkerConnection _worker;
    private readonly RemoteProcessSession _session;

    internal RemoteWindowProcess(WorkerPool pool, WorkerConnection worker, RemoteProcessSession session)
    {
        _pool = pool;
        _worker = worker;
        _session = session;
    }

    /// <summary>Gets the window's process id.</summary>
    public long Pid => _session.Pid;

    /// <summary>Gets the number of the worker the window renders in (as in <c>/proc/kazoos/workers</c>).</summary>
    public int WorkerId => _worker.Id;

    /// <summary>Gets the memory of the window's worker runtime from its last heartbeat, in bytes.</summary>
    public long WorkerMemoryBytes => _worker.RuntimeMemoryBytes;

    /// <summary>Gets the task that ends with the window's process.</summary>
    public Task<RemoteProcessExit> Exit => _session.Exit;

    /// <summary>Sends one input object to the window's runner (a <c>uin</c> message).</summary>
    /// <param name="write">Writes the properties of the object.</param>
    public void Send(Action<Utf8JsonWriter> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (_session.HasEnded)
        {
            return;
        }

        _worker.Post(WorkerMessage.Create(WorkerMessageType.UiInput, writer =>
        {
            writer.WriteNumber("p", _session.Pid);
            writer.WriteStartObject("d");
            write(writer);
            writer.WriteEndObject();
        }));
    }

    /// <summary>
    /// Closes the window: sends <paramref name="close"/> to the runner, waits <see cref="CloseTimeout"/>
    /// for the process to end, then terminates its worker (a frozen window cannot hold its worker).
    /// </summary>
    /// <param name="close">Writes the runner's close input.</param>
    /// <returns>A task that ends when the process has ended.</returns>
    public async Task CloseAsync(Action<Utf8JsonWriter> close)
    {
        Send(close);
        try
        {
            await _session.Exit.WaitAsync(CloseTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _pool.TerminateWorker(_worker, _session.Pid);
        }
    }
}
