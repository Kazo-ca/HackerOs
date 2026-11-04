using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using Kazo.Os.Simulation.Abstractions.Workers;

namespace Kazo.Os.Infrastructure.Browser.Workers;

/// <summary>
/// Real Web Workers (ADR 0070, section 4): the C# half of <c>wwwroot/workerHost.js</c>, which this
/// class imports itself (no host references it, AGENTS.md). Each worker loads the page's own .NET
/// runtime and the assembly named by <see cref="WebWorkerStartOptions.EntryAssembly"/>; messages are
/// strings in both directions.
/// </summary>
/// <remarks>
/// Browser only: registered by the WebAssembly hosts (<c>OS/Kazo.Os.Ecosystem/Program.cs</c>, also
/// run by <c>test/test</c>), never by the server (ADR 0070, section 6). Callbacks arrive on the
/// page's single .NET thread.
/// <para>
/// When the page is cross-origin isolated (ADR 0131, section 4), <c>workerHost.js</c> carries the
/// messages in shared rings by itself; this class adds the synchronous answers
/// (<see cref="ISynchronousWebWorker"/>) and the browser's memory meter (<see cref="IWebWorkerMemoryMeter"/>).
/// </para>
/// </remarks>
[SupportedOSPlatform("browser")]
public sealed partial class BrowserWebWorkerRuntime : IWebWorkerRuntime, IWebWorkerMemoryMeter
{
    private const string ModuleName = "hackeros-worker-host";
    private const string ModulePath = "_content/Kazo.Os.Infrastructure.Browser/workerHost.js";

    private readonly Lazy<Task> _import = new(ImportModuleAsync);

    /// <summary>
    /// Gets the type whose static <c>[JSExport]</c> methods receive a worker's messages, by full
    /// name, inside <see cref="WebWorkerStartOptions.EntryAssembly"/>.
    /// </summary>
    public string EntryType { get; init; } = "Kazo.Os.Platform.WorkerHost.Browser.BrowserWorkerEntryPoint";

    /// <inheritdoc />
    public int HardwareConcurrency
    {
        get
        {
            // The module is imported at startup (AddHackerOsWorkerProcesses); before that, the
            // global navigator answers directly.
            double count = _import.IsValueCreated && _import.Value.IsCompletedSuccessfully
                ? GetHardwareConcurrency()
                : JSHost.GlobalThis.GetPropertyAsJSObject("navigator")?.GetPropertyAsDouble("hardwareConcurrency") ?? 2;
            return Math.Max(1, (int)count);
        }
    }

    /// <summary>Imports <c>workerHost.js</c>; call once at startup so later calls are synchronous.</summary>
    public Task InitializeAsync() => _import.Value;

    /// <inheritdoc />
    public async Task<IWebWorker> StartAsync(WebWorkerStartOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _import.Value.ConfigureAwait(true);
        BrowserWebWorker worker = new();
        worker.Id = StartWorker(options.Name, options.EntryAssembly, EntryType, worker.OnEvent);
        using CancellationTokenRegistration abandon = cancellationToken.Register(worker.Terminate);
        await worker.Ready.ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        return worker;
    }

    /// <inheritdoc />
    public async Task WhenIdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _import.Value.ConfigureAwait(true);
        await WhenIdle((int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue)).WaitAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, long>?> MeasureWorkersAsync(CancellationToken cancellationToken = default)
    {
        await _import.Value.ConfigureAwait(true);
        string json = await MeasureMemory().WaitAsync(cancellationToken).ConfigureAwait(true);
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        Dictionary<int, long> bytes = [];
        using JsonDocument document = JsonDocument.Parse(json);
        foreach (JsonProperty worker in document.RootElement.EnumerateObject())
        {
            if (int.TryParse(worker.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                bytes[id] = (long)worker.Value.GetDouble();
            }
        }

        return bytes;
    }

    private static async Task ImportModuleAsync()
    {
        string baseUri = JSHost.GlobalThis.GetPropertyAsJSObject("document")?.GetPropertyAsString("baseURI") ?? "/";
        await JSHost.ImportAsync(ModuleName, new Uri(new Uri(baseUri), ModulePath).ToString()).ConfigureAwait(true);
    }

    [JSImport("hardwareConcurrency", ModuleName)]
    private static partial int GetHardwareConcurrency();

    [JSImport("whenIdle", ModuleName)]
    private static partial Task WhenIdle(int timeoutMs);

    [JSImport("startWorker", ModuleName)]
    private static partial int StartWorker(
        string name,
        string entryAssembly,
        string entryType,
        [JSMarshalAs<JSType.Function<JSType.String, JSType.String>>] Action<string, string> onEvent);

    [JSImport("post", ModuleName)]
    private static partial void PostToWorker(int id, string message);

    [JSImport("terminate", ModuleName)]
    private static partial void TerminateWorker(int id);

    [JSImport("sharedChannels", ModuleName)]
    private static partial bool SharedChannels();

    [JSImport("answerSync", ModuleName)]
    private static partial string AnswerSync(int id, int callId, string message);

    [JSImport("measureMemory", ModuleName)]
    private static partial Task<string> MeasureMemory();

    /// <summary>One worker started by <c>workerHost.js</c>.</summary>
    private sealed class BrowserWebWorker : IWebWorker, ISynchronousWebWorker
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _terminated;

        public int Id { get; set; }

        public Task Ready => _ready.Task;

        public event Action<string>? MessageReceived;

        public event Action<string>? Faulted;

        public bool IsTerminated => _terminated;

        public bool SupportsSynchronousCalls => !_terminated && SharedChannels();

        public SynchronousAnswerOutcome AnswerSynchronousCall(int callId, string reply) =>
            _terminated ? SynchronousAnswerOutcome.Late : AnswerSync(Id, callId, reply) switch
            {
                "ok" => SynchronousAnswerOutcome.Delivered,
                "too-large" => SynchronousAnswerOutcome.TooLarge,
                _ => SynchronousAnswerOutcome.Late
            };

        public void Post(string message)
        {
            if (!_terminated)
            {
                PostToWorker(Id, message);
            }
        }

        public void Terminate()
        {
            if (_terminated)
            {
                return;
            }

            _terminated = true;
            TerminateWorker(Id);
            _ready.TrySetException(new WebWorkerStartException($"Worker {Id} was terminated."));
        }

        public void OnEvent(string kind, string payload)
        {
            switch (kind)
            {
                case "message" when !_terminated:
                    MessageReceived?.Invoke(payload);
                    break;
                case "ready":
                    _ready.TrySetResult();
                    break;
                case "error":
                    if (!_ready.Task.IsCompleted)
                    {
                        _ready.TrySetException(new WebWorkerStartException(payload));
                        Terminate();
                    }
                    else if (!_terminated)
                    {
                        _terminated = true;
                        TerminateWorker(Id);
                        Faulted?.Invoke(payload);
                    }

                    break;
            }
        }
    }
}
