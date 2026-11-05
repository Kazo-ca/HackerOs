using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Kazo.Os.AppSdk;
using Kazo.Os.Platform.WorkerHost.Protocol;

namespace Kazo.Os.Platform.WorkerHost.Worker;

/// <summary>
/// Renders one window app inside a worker (trial of ADR 0129, verdict in ADR 0133): the X11 client
/// side of a window. The worker host knows nothing of Blazor; it gives the runner the process's
/// remote context and a two-way line to the window's host on the page, and ends the process when
/// <see cref="RunAsync"/> returns.
/// </summary>
/// <remarks>
/// The runner type is named by the OS in the <c>start</c> message (the page's
/// <c>Kazo.Os.Platform.RemoteRendering</c> assembly), so this assembly takes no dependency on the
/// rendering library. It must have a public parameterless constructor.
/// </remarks>
public interface IWorkerWindowRunner
{
    /// <summary>Renders the window until the OS closes it, the process is stopped, or rendering fails.</summary>
    /// <param name="run">The process, its entry point and its line to the page.</param>
    /// <param name="cancellationToken">The process token: cancelled by a stop request or a kill.</param>
    /// <returns>The exit code of the window's process.</returns>
    Task<int> RunAsync(WorkerWindowRun run, CancellationToken cancellationToken);
}

/// <summary>
/// One window process running in a worker, as its <see cref="IWorkerWindowRunner"/> sees it: the
/// remote execution context, the app's root component type, and the <c>ui</c>/<c>uin</c> messages
/// to and from the window's host on the page (ADR 0133).
/// </summary>
public sealed class WorkerWindowRun
{
    private readonly Action<string> _post;
    private readonly Lock _gate = new();
    private readonly Queue<JsonElement> _early = new();
    private Action<JsonElement>? _receiver;

    internal WorkerWindowRun(
        long pid,
        IAppExecutionContext context,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type entryPointType,
        Action<string> post)
    {
        Pid = pid;
        Context = context;
        EntryPointType = entryPointType;
        _post = post;
    }

    /// <summary>Gets the window's process id.</summary>
    public long Pid { get; }

    /// <summary>
    /// Gets the process's execution context: every SDK call made by the app goes to the OS through it,
    /// with the capabilities of the process, as for a command in a worker.
    /// </summary>
    public IAppExecutionContext Context { get; }

    /// <summary>Gets the app's window component (<c>AppDefinition.EntryPoint</c>).</summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    public Type EntryPointType { get; }

    /// <summary>Sends one output object to the window's host on the page (a <c>ui</c> message).</summary>
    /// <param name="write">Writes the properties of the object (the object itself is opened and closed here).</param>
    public void Send(Action<Utf8JsonWriter> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        _post(WorkerMessage.Create(WorkerMessageType.UiOutput, writer =>
        {
            writer.WriteNumber("p", Pid);
            writer.WriteStartObject("d");
            write(writer);
            writer.WriteEndObject();
        }));
    }

    /// <summary>
    /// Sets the handler of the inputs from the page (<c>uin</c>). Inputs that arrived before are handed
    /// to it at once, in order. It runs on the worker's thread, outside any renderer's dispatcher.
    /// </summary>
    /// <param name="receiver">Receives each input object; the element stays valid after the call.</param>
    public void SetReceiver(Action<JsonElement> receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        List<JsonElement> early;
        lock (_gate)
        {
            _receiver = receiver;
            early = [.. _early];
            _early.Clear();
        }

        foreach (JsonElement input in early)
        {
            receiver(input);
        }
    }

    /// <summary>Writes a diagnostic line to <c>/var/log/syslog</c> on the OS side.</summary>
    /// <param name="line">The line.</param>
    public void Log(string line) =>
        _post(WorkerMessage.Create(WorkerMessageType.Log, writer => writer.WriteString("m", $"pid {Pid}: {line}")));

    /// <summary>Hands one <c>uin</c> input to the receiver, or keeps it until one is set.</summary>
    internal void Deliver(JsonElement input)
    {
        Action<JsonElement>? receiver;
        lock (_gate)
        {
            receiver = _receiver;
            if (receiver is null)
            {
                _early.Enqueue(input);
                return;
            }
        }

        receiver(input);
    }
}
