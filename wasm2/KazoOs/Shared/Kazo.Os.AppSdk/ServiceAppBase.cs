using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.ServiceProcess;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.AppSdk;

/// <summary>
/// Explains why the ecosystem requested a session service to stop.
/// </summary>
public enum ServiceStopReason
{
    /// <summary>The user or OS disabled the service.</summary>
    Disabled,

    /// <summary>The authenticated user logged out.</summary>
    Logout,

    /// <summary>The simulated operating system is shutting down.</summary>
    Shutdown,

    /// <summary>The service or one of its dependencies faulted.</summary>
    Faulted,

    /// <summary>
    /// Another process asked it to stop: <c>kill</c>, "End task", <c>systemctl stop</c> (ADR 0069).
    /// </summary>
    Terminated
}

/// <summary>
/// Base class for non-visual work that lasts only for the active OS session.
/// </summary>
public abstract class ServiceAppBase : AppBase
{
    private readonly Lock _stopGate = new();
    private IAppExecutionContext? _context;
    private Task? _stopping;

    /// <summary>Initializes a service application with its validated manifest.</summary>
    /// <param name="manifest">Service application manifest.</param>
    protected ServiceAppBase(AppManifest manifest)
        : base(manifest, AppKind.Service)
    {
    }

    /// <summary>
    /// Initializes a service-like app of another kind: a web site (<see cref="AppKind.Web"/>,
    /// <see cref="WebServerAppBase"/>) has the same lifecycle as a service (ADR 0073).
    /// </summary>
    /// <param name="manifest">Application manifest.</param>
    /// <param name="kind">The kind the manifest must declare.</param>
    private protected ServiceAppBase(AppManifest manifest, AppKind kind)
        : base(manifest, kind)
    {
    }

    /// <summary>
    /// Runs the service until it completes, faults, or receives session cancellation.
    /// </summary>
    /// <param name="context">Scoped application execution context.</param>
    /// <param name="sessionCancellationToken">Signals logout, disable, or OS shutdown.</param>
    /// <returns>A task representing the active service session.</returns>
    /// <remarks>
    /// The service runs inside a <see cref="KazoExecutionContext"/> scope for <paramref name="context"/>
    /// (working directory: the user's home), so the <c>Hack*</c> file API works without passing the
    /// context around (ADR 0043).
    /// </remarks>
    public Task RunAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        lock (_stopGate)
        {
            // A new run gets its own stop: OnStoppingAsync runs once per run, not once per instance.
            _stopping = null;
        }

        // ADR 0069: the OS's stop request (kill, End task, logout...) runs OnStoppingAsync right
        // away, before the token is cancelled, and the OS waits for it within the grace period.
        context.StopRequested += OnStopRequested;
        context.IntentReceived += OnIntentReceived;
        return RunInScopeAsync(context, sessionCancellationToken);
    }

    /// <summary>
    /// Gives the service a bounded opportunity to release in-memory resources.
    /// </summary>
    /// <param name="reason">Reason the ecosystem requested the stop.</param>
    /// <param name="cancellationToken">Enforces the ecosystem shutdown deadline.</param>
    /// <returns>A task representing bounded cleanup.</returns>
    /// <remarks>Runs inside the same ambient context scope as <see cref="RunAsync"/> when the service was started.</remarks>
    /// <remarks>
    /// Runs <see cref="OnStoppingAsync"/> once per run: when the OS already announced the stop
    /// (<see cref="IAppExecutionContext.StopRequested"/>), this call waits for that same cleanup.
    /// </remarks>
    public async ValueTask StopAsync(ServiceStopReason reason, CancellationToken cancellationToken)
    {
        await StopOnceAsync(reason, cancellationToken);
    }

    /// <summary>Maps the OS's stop reason to the one a service sees.</summary>
    /// <param name="reason">Reason carried by the stop request.</param>
    public static ServiceStopReason ToServiceStopReason(Kazo.Os.Simulation.Abstractions.Processes.ProcessExitReason reason) => reason switch
    {
        Kazo.Os.Simulation.Abstractions.Processes.ProcessExitReason.Disabled => ServiceStopReason.Disabled,
        Kazo.Os.Simulation.Abstractions.Processes.ProcessExitReason.Logout => ServiceStopReason.Logout,
        Kazo.Os.Simulation.Abstractions.Processes.ProcessExitReason.Shutdown => ServiceStopReason.Shutdown,
        Kazo.Os.Simulation.Abstractions.Processes.ProcessExitReason.Fault => ServiceStopReason.Faulted,
        _ => ServiceStopReason.Terminated
    };

    private void OnStopRequested(object? sender, Diagnostics.KazoStopRequestedEventArgs e)
    {
        // The grace period is simulated time; the cleanup budget follows it in real time.
        CancellationTokenSource budget = new(e.GracePeriod > TimeSpan.Zero ? e.GracePeriod : TimeSpan.FromSeconds(1));
        Task cleanup = StopOnceAsync(ToServiceStopReason(e.Reason), budget.Token);
        _ = cleanup.ContinueWith(_ => budget.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        e.Defer(cleanup);
    }

    private Task StopOnceAsync(ServiceStopReason reason, CancellationToken cancellationToken)
    {
        lock (_stopGate)
        {
            return _stopping ??= RunStoppingAsync(reason, cancellationToken);
        }
    }

    private async Task RunStoppingAsync(ServiceStopReason reason, CancellationToken cancellationToken)
    {
        using IDisposable? scope = _context is null ? null : KazoExecutionContext.Enter(_context);
        await OnStoppingAsync(reason, cancellationToken);
    }

    private async Task RunInScopeAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken)
    {
        using IDisposable scope = KazoExecutionContext.Enter(context);
        try
        {
            // ADR 0097: "activating" until the start code returns, "active" from then on (READY=1).
            await OnStartAsync(context, sessionCancellationToken);
            context.ServiceNotify.Notify(new ServiceNotification(Ready: true));
            await RunCoreAsync(context, sessionCancellationToken);
        }
        finally
        {
            context.StopRequested -= OnStopRequested;
            context.IntentReceived -= OnIntentReceived;
        }
    }

    /// <summary>
    /// Runs <see cref="OnIntentReceivedAsync"/> for an intent the OS handed to the running service
    /// (ADR 0096, section 5). The OS raises the event inside the service's ambient context; a fault is
    /// logged, never thrown at the sender.
    /// </summary>
    private async void OnIntentReceived(object? sender, AppIntentDelivery intent)
    {
        try
        {
            await OnIntentReceivedAsync(intent, _context?.CancellationToken ?? CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _context?.Logging.Log(
                Kazo.Os.Simulation.Abstractions.Diagnostics.DiagnosticSeverity.Error,
                $"OnIntentReceivedAsync threw {exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// Prepares the service before its loop (ADR 0097, section 1): the service reads as
    /// <c>activating</c> until this returns, then <c>active</c> with health
    /// <see cref="KazoServiceHealth.Healthy"/>. A start longer than the OS's start timeout (30 s) is
    /// killed and the service reads as <c>failed (Result: timeout)</c>; an exception here fails it
    /// with <c>exception</c>. The default does nothing, so the service is active at once.
    /// </summary>
    /// <param name="context">Scoped application execution context.</param>
    /// <param name="sessionCancellationToken">Signals the end of the run.</param>
    /// <returns>A task completing when the service is ready to serve.</returns>
    protected virtual Task OnStartAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Reports the service's own health without stopping it (ADR 0097, section 2), like
    /// <c>sd_notify("STATUS=…")</c>: a site that can no longer reach its database reports
    /// <see cref="KazoServiceHealth.Degraded"/> with a short reason. <c>systemctl status</c> and the
    /// task manager show both.
    /// </summary>
    /// <param name="health">The current health.</param>
    /// <param name="status">A short human-readable status, or <see langword="null"/> to clear it.</param>
    /// <remarks>Does nothing before <see cref="RunAsync"/> or when the OS does not track this process as a service.</remarks>
    protected void ReportHealth(KazoServiceHealth health, string? status = null) =>
        _context?.ServiceNotify.Notify(new ServiceNotification(Health: KazoServiceStates.ToInternal(health), StatusText: status ?? string.Empty));

    /// <summary>Implements the active service loop.</summary>
    /// <param name="context">Scoped application execution context.</param>
    /// <param name="sessionCancellationToken">Signals the end of the OS session.</param>
    /// <returns>A task representing the service loop.</returns>
    protected abstract Task RunCoreAsync(
        IAppExecutionContext context,
        CancellationToken sessionCancellationToken);

    /// <summary>
    /// Handles a bounded stop request: called once per run, when the OS announces the stop
    /// (before the session token is cancelled, ADR 0069) or, at the latest, when the ecosystem stops
    /// the service. A service that holds state finishes or cancels its current operation here.
    /// </summary>
    /// <param name="reason">Reason the ecosystem requested the stop.</param>
    /// <param name="cancellationToken">Enforces the cleanup deadline.</param>
    /// <returns>A task representing cleanup.</returns>
    protected virtual ValueTask OnStoppingAsync(
        ServiceStopReason reason,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>
    /// Handles an intent the OS hands to the running service instead of starting a second one
    /// (ADR 0096, section 5): an app-defined intent it declares (<c>Intents</c> of the manifest), with
    /// its payload, or a launch with arguments. The intent that started the service is
    /// <see cref="IAppExecutionContext.Intent"/>, read in <see cref="RunCoreAsync"/>.
    /// </summary>
    /// <param name="intent">Identifier, payload, sender and arguments.</param>
    /// <param name="cancellationToken">The service's process token.</param>
    /// <returns>A task representing the handling.</returns>
    protected virtual Task OnIntentReceivedAsync(AppIntentDelivery intent, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}