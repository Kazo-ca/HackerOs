using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.Simulation.Abstractions.Diagnostics;

namespace HackerOs.Samples.ServiceApp;

/// <summary>
/// Deterministic background status and ticker service deriving <see cref="ServiceAppBase"/>.
/// </summary>
/// <remarks>
/// Demonstrates non-visual session-scoped service execution:
/// <list type="bullet">
///   <item>Runs an active loop tied to <c>sessionCancellationToken</c>.</item>
///   <item>Publishes health and status ticker events on every tick.</item>
///   <item>Performs bounded cleanup when stopping (<see cref="OnStoppingAsync"/>).</item>
///   <item>Retains no volatile state across session restart.</item>
/// </list>
/// </remarks>
public class SampleTickerService : ServiceAppBase
{
    /// <summary>This app's manifest ID, also the owning namespace for <see cref="SampleTickerTopics"/>.</summary>
    public const string AppId = "org.hackeros.samples.service-app";

    private long _tickCount;
    private bool _isStopping;
    private readonly TimeSpan _tickInterval;

    public SampleTickerService()
        : this(AppDefinitionMapper.ToManifest(new ServiceSampleApp()), TimeSpan.FromSeconds(1))
    {
    }

    /// <summary>
    /// Initializes the service the way the OS creates an app's entry point, with its manifest only
    /// (ADR 0055), in-process or in its worker; it ticks every simulated second.
    /// </summary>
    public SampleTickerService(AppManifest manifest)
        : this(manifest, TimeSpan.FromSeconds(1))
    {
    }

    public SampleTickerService(AppManifest manifest, TimeSpan tickInterval)
        : base(manifest)
    {
        _tickInterval = tickInterval;
    }

    /// <summary>Gets the current in-memory tick count for the active session.</summary>
    public long TickCount => _tickCount;

    /// <summary>Gets whether the service has observed a stop signal.</summary>
    public bool IsStopping => _isStopping;

    /// <inheritdoc />
    protected override async Task RunCoreAsync(
        IAppExecutionContext context,
        CancellationToken sessionCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Reset volatile state on fresh start (P2-SVC-002)
        _tickCount = 0;
        _isStopping = false;

        context.Logging.Log(
            DiagnosticSeverity.Information,
            "SampleTickerService started.",
            new Dictionary<string, string> { ["AppId"] = Manifest.Id });

        try
        {
            while (!sessionCancellationToken.IsCancellationRequested)
            {
                await context.Clock.DelayAsync(_tickInterval, sessionCancellationToken);
                _tickCount++;

                string status = $"OK - Tick #{_tickCount}";
                DateTimeOffset now = context.Clock.UtcNow;

                // Publish status/health event on this app's own topic (P2-SVC-002; migrated to the
                // topic lane per doc/wasm/dev/adr/0038-emitter-authorized-topic-messaging.md — the old
                // type-based Publish<TEvent> no longer delivers app-initiated events).
                context.Events.Publish(SampleTickerTopics.Ticked, new SampleTickerEvent(_tickCount, now, status));

                context.Logging.Log(
                    DiagnosticSeverity.Information,
                    $"SampleTickerService tick #{_tickCount} at {now:o}",
                    new Dictionary<string, string>
                    {
                        ["TickCount"] = _tickCount.ToString(),
                        ["Status"] = status
                    });
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on logout, disable, or OS shutdown
            _isStopping = true;
            context.Logging.Log(
                DiagnosticSeverity.Information,
                "SampleTickerService observed session cancellation.",
                new Dictionary<string, string> { ["FinalTickCount"] = _tickCount.ToString() });
        }
    }

    /// <inheritdoc />
    protected override ValueTask OnStoppingAsync(
        ServiceStopReason reason,
        CancellationToken cancellationToken)
    {
        _isStopping = true;

        // Perform bounded cleanup – clear in-memory state so no volatile work leaks across restart (P2-SVC-002)
        _tickCount = 0;

        return ValueTask.CompletedTask;
    }
}
