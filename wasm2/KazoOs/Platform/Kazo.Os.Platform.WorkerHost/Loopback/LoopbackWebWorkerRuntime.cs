using System.Collections.Concurrent;
using System.Threading.Channels;
using Kazo.Os.Platform.WorkerHost.Worker;
using Kazo.Os.Simulation.Abstractions.Workers;

namespace Kazo.Os.Platform.WorkerHost.Loopback;

/// <summary>
/// An in-process stand-in for Web Workers, for tests and for checking the protocol without a
/// browser: each "worker" is a dedicated thread with a single-threaded synchronization context
/// running a real <see cref="WorkerProcessHost"/>, and every message crosses as JSON text, exactly as
/// with <c>postMessage</c>.
/// </summary>
/// <remarks>
/// Like a real worker, a loopback worker runs one thing at a time: a process that never yields keeps
/// its thread, its heartbeat stops, and only <see cref="IWebWorker.Terminate"/> stops it from the
/// OS's point of view (the stuck thread is a background thread, abandoned). It does not meter CPU:
/// the context-switch hook is a static of the process, shared with the OS side.
/// </remarks>
public sealed class LoopbackWebWorkerRuntime : IWebWorkerRuntime
{
    private readonly Func<IWorkerAssemblyLoader> _loaderFactory;
    private readonly ConcurrentBag<LoopbackWorker> _workers = [];
    private int _nextId;

    /// <summary>Initializes the runtime.</summary>
    /// <param name="hardwareConcurrency">The <c>navigator.hardwareConcurrency</c> to report.</param>
    /// <param name="startupDelay">Simulated cold start of each worker.</param>
    /// <param name="loaderFactory">Creates each worker's assembly loader (default: already-loaded assemblies).</param>
    public LoopbackWebWorkerRuntime(int hardwareConcurrency = 4, TimeSpan? startupDelay = null, Func<IWorkerAssemblyLoader>? loaderFactory = null)
        : this(hardwareConcurrency, startupDelay, loaderFactory, synchronousCalls: false)
    {
    }

    /// <summary>Initializes the runtime, optionally as on a cross-origin isolated page.</summary>
    /// <param name="hardwareConcurrency">The <c>navigator.hardwareConcurrency</c> to report.</param>
    /// <param name="startupDelay">Simulated cold start of each worker.</param>
    /// <param name="loaderFactory">Creates each worker's assembly loader (default: already-loaded assemblies).</param>
    /// <param name="synchronousCalls">
    /// Whether workers may wait for the OS's answer (ADR 0131, section 4): the worker's thread blocks
    /// on an event, as a real worker blocks in <c>Atomics.wait</c>.
    /// </param>
    public LoopbackWebWorkerRuntime(int hardwareConcurrency, TimeSpan? startupDelay, Func<IWorkerAssemblyLoader>? loaderFactory, bool synchronousCalls)
    {
        SynchronousCalls = synchronousCalls;
        HardwareConcurrency = hardwareConcurrency;
        StartupDelay = startupDelay ?? TimeSpan.Zero;
        _loaderFactory = loaderFactory ?? (static () => new ResolvedAssemblyLoader());
    }

    /// <inheritdoc />
    public int HardwareConcurrency { get; }

    /// <summary>Gets whether workers may call the OS synchronously (ADR 0131, section 4).</summary>
    public bool SynchronousCalls { get; }

    /// <summary>Gets the simulated cold start of each worker.</summary>
    public TimeSpan StartupDelay { get; }

    /// <summary>Gets how many workers were started.</summary>
    public int StartedCount => Volatile.Read(ref _nextId);

    /// <summary>Gets how many workers are alive (started and not terminated).</summary>
    public int AliveCount => _workers.Count(static worker => !worker.IsTerminated);

    /// <summary>Gets the options each worker was started with, in start order.</summary>
    public IReadOnlyList<WebWorkerStartOptions> StartedWith => [.. _workers.OrderBy(static worker => worker.Id).Select(static worker => worker.Options)];

    /// <summary>Gets whether <see cref="WhenIdleAsync"/> was awaited (the "system started" path).</summary>
    public bool IdleWaited { get; private set; }

    /// <inheritdoc />
    public async Task<IWebWorker> StartAsync(WebWorkerStartOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (StartupDelay > TimeSpan.Zero)
        {
            await Task.Delay(StartupDelay, cancellationToken).ConfigureAwait(false);
        }

        LoopbackWorker worker = new(Interlocked.Increment(ref _nextId), options, _loaderFactory(), SynchronousCalls);
        _workers.Add(worker);
        return worker;
    }

    /// <inheritdoc />
    public Task WhenIdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        IdleWaited = true;
        return Task.CompletedTask;
    }

    /// <summary>One loopback worker: a thread, its message loop and its worker host.</summary>
    private sealed class LoopbackWorker : IWebWorker, ISynchronousWebWorker
    {
        private readonly WorkerThreadContext _thread;
        private readonly Channel<string> _toOs = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        private readonly WorkerProcessHost _host;
        private readonly bool _synchronousCalls;
        private readonly Lock _syncGate = new();
        private (int Id, string? Reply, ManualResetEventSlim Answered)? _waiting;
        private volatile bool _terminated;

        public LoopbackWorker(int id, WebWorkerStartOptions options, IWorkerAssemblyLoader loader, bool synchronousCalls)
        {
            Id = id;
            Options = options;
            _synchronousCalls = synchronousCalls;
            _host = new WorkerProcessHost(PostToOs, loader, canMeterCpu: false, synchronousCalls ? CallSync : null);
            _thread = new WorkerThreadContext(options.Name);
            _ = PumpToOsAsync();
        }

        public int Id { get; }

        public WebWorkerStartOptions Options { get; }

        public event Action<string>? MessageReceived;

        public event Action<string>? Faulted
        {
            add { }
            remove { }
        }

        public bool IsTerminated => _terminated;

        public bool SupportsSynchronousCalls => _synchronousCalls && !_terminated;

        public SynchronousAnswerOutcome AnswerSynchronousCall(int callId, string reply)
        {
            lock (_syncGate)
            {
                if (_terminated || _waiting is not { } waiting || waiting.Id != callId)
                {
                    return SynchronousAnswerOutcome.Late;
                }

                _waiting = waiting with { Reply = reply };
                waiting.Answered.Set();
                return SynchronousAnswerOutcome.Delivered;
            }
        }

        /// <summary>The worker's thread blocks until the OS answers, as in <c>Atomics.wait</c>.</summary>
        private string? CallSync(int callId, string message, TimeSpan timeout)
        {
            using ManualResetEventSlim answered = new();
            lock (_syncGate)
            {
                _waiting = (callId, null, answered);
            }

            PostToOs(message);
            answered.Wait(timeout);
            lock (_syncGate)
            {
                string? reply = _waiting is { } waiting && waiting.Id == callId ? waiting.Reply : null;
                _waiting = null;
                return reply;
            }
        }

        public void Post(string message)
        {
            if (!_terminated)
            {
                _thread.Post(_ => _host.Receive(message), null);
            }
        }

        public void Terminate()
        {
            if (_terminated)
            {
                return;
            }

            _terminated = true;
            _host.Shutdown();
            _toOs.Writer.TryComplete();
            _thread.Stop();
        }

        private void PostToOs(string message)
        {
            if (!_terminated)
            {
                _toOs.Writer.TryWrite(message);
            }
        }

        /// <summary>Delivers the worker's messages to the OS in order, off the worker's thread.</summary>
        private async Task PumpToOsAsync()
        {
            await foreach (string message in _toOs.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (_terminated)
                {
                    return;
                }

                MessageReceived?.Invoke(message);
            }
        }
    }

    /// <summary>
    /// A single-threaded synchronization context on its own background thread: every callback,
    /// message and continuation of the worker runs here, one at a time, like a worker's event loop.
    /// </summary>
    private sealed class WorkerThreadContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
        private readonly Thread _thread;

        public WorkerThreadContext(string name)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = name };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                _queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // Stopped (terminated): the event loop is gone.
            }
        }

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new NotSupportedException("A worker's event loop cannot be entered synchronously.");

        public override SynchronizationContext CreateCopy() => this;

        public void Stop() => _queue.CompleteAdding();

        private void Run()
        {
            SetSynchronizationContext(this);
            foreach ((SendOrPostCallback callback, object? state) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    callback(state);
                }
                catch (Exception)
                {
                    // An unhandled error in a worker's event loop is reported by the browser, not rethrown.
                }
            }
        }
    }
}
