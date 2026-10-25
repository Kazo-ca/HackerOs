using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Notifications;
using HackerOs.Simulation.Abstractions.Processes;

namespace HackerOs.Simulation.Abstractions.Gateways;

/// <summary>
/// Thrown when a gateway denies an operation because trusted policy did not grant the
/// required capability or authority for the calling app instance.
/// </summary>
public sealed class AppGatewayAccessDeniedException : Exception
{
    /// <summary>Initializes a denial exception carrying the stable policy evaluation.</summary>
    public AppGatewayAccessDeniedException(string capability, CapabilityPolicyEvaluation evaluation)
        : base($"Capability '{capability}' was denied ({evaluation.Reason}).")
    {
        Capability = capability;
        Evaluation = evaluation;
    }

    /// <summary>Gets the exact capability that was evaluated.</summary>
    public string Capability { get; }

    /// <summary>Gets the stable deny-by-default policy evaluation.</summary>
    public CapabilityPolicyEvaluation Evaluation { get; }
}

/// <summary>
/// Evaluates capability policy for one bound app/user/authority, without exposing the
/// underlying grant repository or any other app's grants.
/// </summary>
public interface ICapabilityChecker
{
    /// <summary>Evaluates a deny-by-default decision for one capability and optional resource.</summary>
    /// <param name="capability">Exact capability identifier required by the operation.</param>
    /// <param name="requiredAuthority">Minimum authority policy requires, independent of the grant.</param>
    /// <param name="resourceCandidate">Optional concrete resource checked against structured grant constraints.</param>
    CapabilityPolicyEvaluation Evaluate(
        string capability,
        AppAuthority requiredAuthority = AppAuthority.User,
        CapabilityResourceCandidate? resourceCandidate = null);

    /// <summary>Evaluates a capability and throws <see cref="AppGatewayAccessDeniedException"/> when denied.</summary>
    /// <param name="capability">Exact capability identifier required by the operation.</param>
    /// <param name="requiredAuthority">Minimum authority policy requires, independent of the grant.</param>
    /// <param name="resourceCandidate">Optional concrete resource checked against structured grant constraints.</param>
    void Require(
        string capability,
        AppAuthority requiredAuthority = AppAuthority.User,
        CapabilityResourceCandidate? resourceCandidate = null);
}

/// <summary>
/// Carries one permission-class filesystem denial (missing capability, missing authority, or a
/// denied selected-handle/mode check) raised through <see cref="IAppPermissionErrorGateway"/>.
/// </summary>
public sealed class AppPermissionErrorEventArgs(FileSystemError error) : EventArgs
{
    /// <summary>Gets the permission-class failure that triggered this notification.</summary>
    public FileSystemError Error { get; } = error ?? throw new ArgumentNullException(nameof(error));

    /// <summary>
    /// Gets or sets whether a subscriber already surfaced this error to the user. Left
    /// <see langword="false"/>, the window host shows a default error notification after every
    /// subscriber has run.
    /// </summary>
    public bool ErrorHandled { get; set; }
}

/// <summary>
/// Notifies subscribers when any gateway bound to this app instance denies an operation for a
/// permission-class reason, so the window host can surface a default error notification when no
/// app-level handler marks the error as already handled.
/// </summary>
public interface IAppPermissionErrorGateway
{
    /// <summary>Raised when a permission-class error occurs on a gateway bound to this app instance.</summary>
    event EventHandler<AppPermissionErrorEventArgs>? PermissionDenied;
}

/// <summary>
/// Provides one app instance's authorized filesystem access. Every call is evaluated against
/// trusted OS policy for the bound app/user; the gateway never exposes the raw repository.
/// </summary>
public interface IAppFileSystemGateway
{
    /// <summary>
    /// Opens a charge for the in-memory buffer of a write stream (<c>HackFileStream</c>), counted in
    /// the process's kernel memory when memory is measured (ADR 0068). The stream resizes it as its
    /// buffer grows and disposes it when it is released.
    /// </summary>
    /// <returns>The charge; <see cref="KernelMemoryCharge.None"/> when the OS does not measure memory.</returns>
    KernelMemoryCharge ChargeStreamBuffer() => KernelMemoryCharge.None;

    /// <summary>
    /// Opens one descriptor of the calling process for <paramref name="path"/> and holds it until the
    /// returned handle is disposed (ADR 0125, <c>RLIMIT_NOFILE</c>): what a write stream
    /// (<c>HackFileStream</c>) holds while it buffers. A read handle from <see cref="ReadAsync"/>
    /// already holds its own descriptor.
    /// </summary>
    /// <param name="path">The file the descriptor designates.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The handle, or <see cref="FileSystemErrorCode.TooManyOpenFiles"/> (<c>EMFILE</c>). Defaults to a
    /// handle that holds nothing, for gateways that do not count descriptors.
    /// </returns>
    ValueTask<FileSystemResult<IDisposable>> OpenDescriptorAsync(VirtualPath path, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(FileSystemResult<IDisposable>.Success(NoDescriptor.Instance));

    /// <summary>Opens streamed regular-file content.</summary>
    ValueTask<FileSystemResult<FileSystemContentReadHandle>> ReadAsync(
        FileSystemReadRequest request, CancellationToken cancellationToken = default);

    /// <summary>Enumerates immediate directory children in ordinal name order.</summary>
    ValueTask<FileSystemResult<FileSystemDirectorySnapshot>> EnumerateAsync(
        FileSystemEnumerateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Creates an empty file, directory, or symbolic link atomically.</summary>
    ValueTask<FileSystemMutationResult> CreateAsync(
        FileSystemCreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Streams and atomically replaces regular-file content.</summary>
    ValueTask<FileSystemMutationResult> WriteAsync(
        FileSystemWriteRequest request, IFileSystemContentSource content, CancellationToken cancellationToken = default);

    /// <summary>Moves or renames an entry atomically.</summary>
    ValueTask<FileSystemMutationResult> MoveAsync(
        FileSystemMoveRequest request, CancellationToken cancellationToken = default);

    /// <summary>Copies an entry or subtree atomically.</summary>
    ValueTask<FileSystemMutationResult> CopyAsync(
        FileSystemCopyRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes an entry or subtree atomically.</summary>
    ValueTask<FileSystemMutationResult> DeleteAsync(
        FileSystemDeleteRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads one immutable entry snapshot.</summary>
    ValueTask<FileSystemResult<FileSystemEntrySnapshot>> StatAsync(
        FileSystemStatRequest request, CancellationToken cancellationToken = default);

    /// <summary>Atomically replaces owner/group/other permission bits.</summary>
    ValueTask<FileSystemMutationResult> SetPermissionsAsync(
        FileSystemSetPermissionsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a trusted, short-lived selected-resource handle (e.g. from a file-open dialog) to
    /// every operation issued by the returned scoped gateway, in place of a broad capability.
    /// </summary>
    /// <param name="handle">Handle previously issued to this exact app/user.</param>
    IAppFileSystemGateway WithSelectedHandle(FileSystemSelectedResourceHandle handle);
}

/// <summary>
/// Provides one app instance's typed event bus access. Subscriptions are attributed to the
/// app instance so platform code can trace and, if necessary, force-dispose leaked subscriptions.
/// </summary>
/// <remarks>
/// Per <c>doc/wasm/dev/adr/0038-emitter-authorized-topic-messaging.md</c>, this gateway has two lanes.
/// <see cref="Subscribe{TEvent}(Action{TEvent})"/>/<see cref="Publish{TEvent}(TEvent)"/> are the kernel lane: any app may still
/// subscribe to a trusted platform lifecycle event type (read access was never the security problem),
/// but <see cref="Publish{TEvent}(TEvent)"/> no longer forwards to the underlying bus for app-facing callers —
/// no CLR event type is currently allow-listed for app-initiated publish, since the one app that
/// previously used it (<c>SampleTickerService</c>) migrated to the topic lane below. The topic-bus
/// members (<see cref="Subscribe{TPayload}(TopicName, Action{TopicMessage{TPayload}})"/>,
/// <see cref="SubscribeChannel{TPayload}"/>, <see cref="Publish{TPayload}(TopicName, TPayload)"/>,
/// <see cref="RegisterSharedChannel"/>) are the app lane apps are expected to use instead: publishing is
/// authorized by namespace ownership or shared-channel policy rather than being unrestricted.
/// </remarks>
public interface IAppEventGateway
{
    /// <summary>Subscribes to every event of type <typeparamref name="TEvent"/>.</summary>
    IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull;

    /// <summary>
    /// Publishes one event to every current subscriber of <typeparamref name="TEvent"/>, when
    /// <typeparamref name="TEvent"/> is on the app-publishable allow-list. No event type is currently
    /// allow-listed; use <see cref="Publish{TPayload}(TopicName, TPayload)"/> instead. A non-allow-listed
    /// publish is silently denied (returns an empty result, matching the existing fault-isolation
    /// convention) rather than throwing.
    /// </summary>
    IReadOnlyList<EventDispatchFault> Publish<TEvent>(TEvent @event) where TEvent : notnull;

    /// <summary>Subscribes to every message published on <paramref name="topic"/>.</summary>
    IDisposable Subscribe<TPayload>(TopicName topic, Action<TopicMessage<TPayload>> handler) where TPayload : notnull;

    /// <summary>
    /// Returns a disposable, <see cref="System.Threading.Channels.Channel{T}"/>-backed subscription for
    /// <paramref name="topic"/>. Disposing stops delivery and completes the channel.
    /// </summary>
    ITopicChannelSubscription<TPayload> SubscribeChannel<TPayload>(TopicName topic, int? boundedCapacity = null)
        where TPayload : notnull;

    /// <summary>
    /// Publishes one message to <paramref name="topic"/>, stamping this instance's own app/user/process
    /// identity as the publisher — never a caller-supplied identity. Denied when <paramref name="topic"/>
    /// is outside this app's own namespace and is not a shared channel this app may publish to.
    /// </summary>
    TopicPublishResult Publish<TPayload>(TopicName topic, TPayload payload) where TPayload : notnull;

    /// <summary>
    /// Asynchronous form of <see cref="Publish{TPayload}(TopicName, TPayload)"/>, with the same rules
    /// and the OS's real result everywhere (ADR 0145). A process running in a Web Worker (ADR 0070)
    /// cannot wait synchronously for the OS, so its <see cref="Publish{TPayload}(TopicName, TPayload)"/>
    /// returns a provisional <see cref="TopicPublishOutcome.Delivered"/> and logs a refusal (ADR 0138,
    /// section 3); this member waits for the OS's answer instead. Defaults to
    /// <see cref="Publish{TPayload}(TopicName, TPayload)"/> so existing implementations keep compiling.
    /// </summary>
    /// <returns>The outcome decided by the OS and the faults of its subscribers.</returns>
    Task<TopicPublishResult> PublishAsync<TPayload>(TopicName topic, TPayload payload, CancellationToken cancellationToken = default)
        where TPayload : notnull
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Publish(topic, payload));
    }

    /// <summary>
    /// Idempotently registers <paramref name="root"/> as a shared channel owned by this app instance.
    /// </summary>
    void RegisterSharedChannel(TopicName root, SharedChannelPolicy policy);
}

/// <summary>
/// Provides one app instance's authorized notification posting, requiring
/// <see cref="AppCapabilities.NotificationsPost"/>.
/// </summary>
public interface IAppNotificationGateway
{
    /// <summary>Posts a notification scoped to the acting user.</summary>
    /// <exception cref="AppGatewayAccessDeniedException">Policy denies <see cref="AppCapabilities.NotificationsPost"/>.</exception>
    NotificationId Post(
        NotificationSeverity severity,
        string title,
        string message,
        IReadOnlyList<NotificationAction>? actions = null,
        TimeSpan? expiresAfter = null);
}

/// <summary>Provides one app instance's structured diagnostic logging, stamped with app identity.</summary>
public interface IAppLoggingGateway
{
    /// <summary>Records one diagnostic entry attributed to the calling app.</summary>
    void Log(DiagnosticSeverity severity, string message, IReadOnlyDictionary<string, string>? properties = null);

    /// <summary>
    /// Appends one raw line to a daemon log file of its own, <c>/var/log/&lt;directory&gt;/&lt;file&gt;.log</c>,
    /// like <c>nginx</c> writing <c>/var/log/nginx/access.log</c> (ADR 0080, section 3). Only a system
    /// app may do so; the platform's log writer does the actual write (ADR 0045: an app never writes
    /// under <c>/var/log</c> itself), with the same batching and rotation as every other log.
    /// </summary>
    /// <param name="relativePath">
    /// <c>&lt;directory&gt;/&lt;file&gt;.log</c>, for example <c>hackweb/docs.access.log</c>. The
    /// directory may not be <c>apps</c>, which belongs to <see cref="Log"/>.
    /// </param>
    /// <param name="line">The line, written as is (a line break inside it is escaped).</param>
    /// <exception cref="UnauthorizedAccessException">The app is not a system app, or the host offers no daemon logs.</exception>
    /// <exception cref="ArgumentException">The path is not a valid daemon log path.</exception>
    void AppendToLogFile(string relativePath, string line) =>
        throw new UnauthorizedAccessException("Daemon log files are not offered by this host.");

    /// <summary>
    /// Appends one line to the debug log of the calling web site, <c>/var/log/hackweb/&lt;app id&gt;.log</c>
    /// (ADR 0073 section 3, ADR 0088 section 5). Only an app of kind <c>Web</c> may do so, when the site
    /// enabled its debug log; the platform's log writer does the write, with the usual rotation.
    /// </summary>
    /// <param name="line">The line, written as is (a line break inside it is escaped).</param>
    /// <exception cref="UnauthorizedAccessException">The app is not a web app, or the host offers no site logs.</exception>
    void AppendToSiteDebugLog(string line) =>
        throw new UnauthorizedAccessException("Site debug logs are not offered by this host.");
}

/// <summary>Provides one app instance's read-only deterministic simulation clock access.</summary>
public interface IAppClockGateway
{
    /// <summary>Gets the current simulated UTC time.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Gets the current monotonic tick number.</summary>
    long CurrentTick { get; }

    /// <summary>Schedules a callback to run once simulated time reaches at least <paramref name="delay"/> from now.</summary>
    IDisposable Schedule(TimeSpan delay, Action callback);

    /// <summary>Returns a task that completes once simulated time reaches at least <paramref name="delay"/> from now.</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

/// <summary>
/// Provides one app instance's authorized process/job access. An app may always observe and
/// stop its own process; observing or managing other processes requires
/// <see cref="AppCapabilities.ProcessList"/>/<see cref="AppCapabilities.ProcessManage"/>.
/// </summary>
public interface IAppProcessGateway
{
    /// <summary>Gets the process record hosting the calling app instance.</summary>
    ProcessRecord OwnProcess { get; }

    /// <summary>Starts a new child process under the calling app's own process.</summary>
    /// <exception cref="AppGatewayAccessDeniedException">Policy denies <see cref="AppCapabilities.ProcessManage"/>.</exception>
    ProcessRecord StartChild(string appId, AppInstanceId appInstanceId, AppKind kind, ResourceProfile resourceProfile);

    /// <summary>Gets every active process; requires <see cref="AppCapabilities.ProcessList"/> to see processes other than the app's own.</summary>
    IReadOnlyList<ProcessRecord> ListProcesses();

    /// <summary>Requests a graceful stop of one process; requires <see cref="AppCapabilities.ProcessManage"/> unless it is the app's own process.</summary>
    /// <exception cref="AppGatewayAccessDeniedException">Policy denies management of another app's process.</exception>
    Task<ProcessRecord> StopAsync(
        ProcessId pid, TimeSpan timeout, ProcessExitReason reason = ProcessExitReason.CloseRequested, CancellationToken cancellationToken = default);

    /// <summary>Immediately force-stops one process; requires <see cref="AppCapabilities.ProcessManage"/> unless it is the app's own process.</summary>
    /// <exception cref="AppGatewayAccessDeniedException">Policy denies management of another app's process.</exception>
    ProcessRecord Kill(ProcessId pid, ProcessExitReason reason = ProcessExitReason.Killed);

    /// <summary>
    /// Asynchronous form of <see cref="Kill"/>, used by <c>HackProcess.KillAsync</c>. A process running
    /// in a Web Worker (ADR 0070) cannot wait synchronously for the OS: its gateway answers here.
    /// Defaults to <see cref="Kill"/> so existing implementations keep compiling.
    /// </summary>
    /// <exception cref="AppGatewayAccessDeniedException">Policy denies management of another app's process.</exception>
    Task<ProcessRecord> KillAsync(ProcessId pid, ProcessExitReason reason = ProcessExitReason.Killed, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Kill(pid, reason));
    }

    /// <summary>
    /// Gets the niceness of one visible process (ADR 0127), like <c>getpriority(2)</c>. Defaults to
    /// <see cref="ProcessNice.Default"/> so existing implementations keep compiling.
    /// </summary>
    /// <param name="pid">A process the app may see (its own, or any with <see cref="AppCapabilities.ProcessList"/>).</param>
    /// <exception cref="KeyNotFoundException">No such active process.</exception>
    /// <exception cref="AppGatewayAccessDeniedException">The process is another app's and policy denies <see cref="AppCapabilities.ProcessList"/>.</exception>
    int GetNice(ProcessId pid) => ProcessNice.Default;

    /// <summary>
    /// Sets the niceness of one process (ADR 0127), like <c>setpriority(2)</c>: <c>renice</c> and
    /// <c>nice</c> come here. The rules are Linux's: a process of the same user may be made nicer
    /// (higher value) freely, with <see cref="AppCapabilities.ProcessManage"/> when it is not the
    /// caller's own; lowering a niceness, or touching another user's process, needs root
    /// (<c>sudo</c>).
    /// </summary>
    /// <param name="pid">The target process.</param>
    /// <param name="nice">The new niceness, clamped to <see cref="ProcessNice.Min"/>..<see cref="ProcessNice.Max"/>.</param>
    /// <returns>The niceness now in effect.</returns>
    /// <exception cref="KeyNotFoundException">No such active process.</exception>
    /// <exception cref="AppGatewayAccessDeniedException">Policy denies <see cref="AppCapabilities.ProcessManage"/> for another process.</exception>
    /// <exception cref="UnauthorizedAccessException">Lowering the niceness, or another user's process, without root (<c>EPERM</c>).</exception>
    /// <exception cref="NotSupportedException">This host does not offer priorities (a worker without the gateway).</exception>
    int SetNice(ProcessId pid, int nice) => throw new NotSupportedException("Process priorities are not offered by this host.");

    /// <summary>
    /// Changes one resource limit of a process (ADR 0125, section 4, <c>setrlimit</c>/<c>prlimit</c>):
    /// the calling app's own process, or with <see cref="AppCapabilities.ProcessManage"/> another
    /// process of the same user, or any process when the caller is elevated (<c>sudo</c>). Only an
    /// elevated caller raises a hard limit.
    /// </summary>
    /// <param name="pid">The process.</param>
    /// <param name="kind">The resource.</param>
    /// <param name="limit">The new soft and hard values.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome, with its <c>errno</c>.</returns>
    /// <exception cref="AppGatewayAccessDeniedException">Policy denies management of another app's process.</exception>
    /// <exception cref="NotSupportedException">This gateway does not manage limits (the default).</exception>
    Task<ResourceLimitChangeResult> SetResourceLimitAsync(
        ProcessId pid, ResourceLimitKind kind, ResourceLimit limit, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This process gateway does not manage resource limits.");
}

/// <summary>The descriptor handle of a gateway that does not count descriptors: disposing it does nothing.</summary>
public sealed class NoDescriptor : IDisposable
{
    private NoDescriptor()
    {
    }

    /// <summary>Gets the shared instance.</summary>
    public static NoDescriptor Instance { get; } = new();

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>Identifies the stable outcome of one <see cref="IAppIntentGateway.LaunchAsync"/> request.</summary>
public enum AppIntentLaunchOutcome
{
    /// <summary>The target app was launched, or an existing single-instance was focused.</summary>
    Launched,

    /// <summary>No catalog app matches the requested target ID.</summary>
    NotFound,

    /// <summary>The resolved target app is currently disabled.</summary>
    Disabled,

    /// <summary>The resolved target's entry point faulted while starting.</summary>
    Faulted
}

/// <summary>Contains the result of one <see cref="IAppIntentGateway.LaunchAsync"/> request.</summary>
/// <param name="Outcome">Stable launch outcome.</param>
/// <param name="ErrorCode">Stable machine-readable error code when the outcome did not succeed.</param>
public sealed record AppIntentLaunchResult(AppIntentLaunchOutcome Outcome, string? ErrorCode = null);

/// <summary>Identifies the stable outcome of one <see cref="IAppIntentGateway.OpenFileAsync"/> request.</summary>
public enum AppIntentOpenFileOutcome
{
    /// <summary>A handler was resolved (explicit preference, configured default, or sole candidate) and launched.</summary>
    Opened,

    /// <summary>Multiple enabled apps can handle this file; the caller must show a chooser and retry via <see cref="IAppIntentGateway.LaunchAsync"/>.</summary>
    ChooserRequired,

    /// <summary>No enabled app declares handling this file.</summary>
    NoHandler,

    /// <summary>The resolved target's entry point faulted while starting.</summary>
    Faulted
}

/// <summary>Contains the result of one <see cref="IAppIntentGateway.OpenFileAsync"/> request.</summary>
/// <param name="Outcome">Stable resolution outcome.</param>
/// <param name="CandidateAppIds">Every candidate app ID considered, populated only for <see cref="AppIntentOpenFileOutcome.ChooserRequired"/>.</param>
/// <param name="ErrorCode">Stable machine-readable error code when the outcome did not succeed.</param>
/// <param name="FellBackToOpen">
/// For <see cref="IAppIntentGateway.EditFileAsync"/>: no handler declares <c>edit</c>, so the file was
/// resolved among the <c>open</c> handlers instead (ADR 0096, section 2).
/// </param>
public sealed record AppIntentOpenFileResult(
    AppIntentOpenFileOutcome Outcome,
    IReadOnlyList<string>? CandidateAppIds = null,
    string? ErrorCode = null,
    bool FellBackToOpen = false);

/// <summary>Identifies the stable outcome of one reveal, URI, app-settings or app-defined intent (ADR 0096).</summary>
public enum AppIntentOutcome
{
    /// <summary>The handler was launched, or the running single instance received the intent.</summary>
    Dispatched,

    /// <summary>Several apps can handle it and none is the default: the caller asks the user, then sends again with a target.</summary>
    ChooserRequired,

    /// <summary>No installed app handles it (no folder handler, no handler for the URI scheme, nobody declares the intent).</summary>
    NoHandler,

    /// <summary>The file, folder or target app does not exist.</summary>
    NotFound,

    /// <summary>The acting user may not read what the intent points at.</summary>
    AccessDenied,

    /// <summary>The target app is uninstalled.</summary>
    Disabled,

    /// <summary>The request itself is malformed: identifier, URI or payload.</summary>
    Invalid,

    /// <summary>The handler's entry point faulted while starting.</summary>
    Faulted
}

/// <summary>Contains the result of one reveal, URI, app-settings or app-defined intent (ADR 0096).</summary>
/// <param name="Outcome">Stable outcome.</param>
/// <param name="CandidateAppIds">The apps to choose from, for <see cref="AppIntentOutcome.ChooserRequired"/>.</param>
/// <param name="ErrorCode">Stable machine-readable error code when the outcome did not succeed (e.g. <c>intent.open-uri.no-handler</c>).</param>
public sealed record AppIntentResult(
    AppIntentOutcome Outcome,
    IReadOnlyList<string>? CandidateAppIds = null,
    string? ErrorCode = null);

/// <summary>
/// Provides one app instance's authorized ability to launch another installed application or
/// open a file, per <see cref="AppCapabilities.AppsLaunch"/>. This is the single, kernel-owned
/// entry point for "run something" -- launching an app outright, opening a file with its
/// resolved default handler, or opening a file with one specific app -- so that responsibility
/// never needs to be reimplemented by individual apps (e.g. a file manager only ever calls this
/// gateway; it never resolves handlers or starts processes itself).
/// </summary>
public interface IAppIntentGateway
{
    /// <summary>
    /// Requests that another installed app be launched with the given arguments. Passing a file
    /// path as an argument (e.g. from an "Open With" picker) opens that file with exactly the
    /// requested app, bypassing file-association resolution.
    /// </summary>
    /// <exception cref="AppGatewayAccessDeniedException">Policy denies <see cref="AppCapabilities.AppsLaunch"/>.</exception>
    ValueTask<AppIntentLaunchResult> LaunchAsync(
        string appId, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests that a file be opened using the system's file-association resolution (an
    /// administrator-configured default, then any manifest-declared candidate). Never guesses:
    /// when more than one app can handle the file, returns <see cref="AppIntentOpenFileOutcome.ChooserRequired"/>
    /// with every candidate so the caller can show a picker and open with an explicit choice via
    /// <see cref="LaunchAsync"/>.
    /// </summary>
    /// <param name="path">Canonical virtual filesystem path to open.</param>
    /// <param name="mediaType">
    /// Caller-supplied media type when already known (e.g. <c>"inode/directory"</c> for a directory-open
    /// request per ADR 0039); never auto-detected from content.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="AppGatewayAccessDeniedException">Policy denies <see cref="AppCapabilities.AppsLaunch"/>.</exception>
    ValueTask<AppIntentOpenFileResult> OpenFileAsync(
        VirtualPath path, string? mediaType = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests that a file be edited (ADR 0096, section 2): resolved like <see cref="OpenFileAsync"/>
    /// among the handlers declaring <c>edit</c>, or among the <c>open</c> handlers when none does
    /// (<see cref="AppIntentOpenFileResult.FellBackToOpen"/>).
    /// </summary>
    /// <param name="path">Canonical virtual filesystem path to edit.</param>
    /// <param name="mediaType">Caller-supplied media type when already known.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="AppGatewayAccessDeniedException">Policy denies <see cref="AppCapabilities.AppsLaunch"/>.</exception>
    ValueTask<AppIntentOpenFileResult> EditFileAsync(
        VirtualPath path, string? mediaType = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This intents gateway does not support editing files.");

    /// <summary>
    /// Requests that the default folder handler open the parent of <paramref name="path"/> and select
    /// it (ADR 0096, section 1; <c>org.freedesktop.FileManager1.ShowItems</c>): the handler starts with
    /// <c>&lt;parent&gt; --select=&lt;path&gt;</c>.
    /// </summary>
    /// <exception cref="AppGatewayAccessDeniedException">Policy denies <see cref="AppCapabilities.AppsLaunch"/>.</exception>
    ValueTask<AppIntentResult> RevealFileAsync(VirtualPath path, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This intents gateway does not support revealing files.");

    /// <summary>
    /// Requests that a URI be opened by the handler of its scheme (ADR 0096, section 3), the media
    /// type <c>x-scheme-handler/&lt;scheme&gt;</c>; <c>file://</c> opens the file.
    /// </summary>
    /// <param name="uri">Absolute URI.</param>
    /// <param name="preferredAppId">The app to use, when the caller already chose one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="AppGatewayAccessDeniedException">Policy denies <see cref="AppCapabilities.AppsLaunch"/>.</exception>
    ValueTask<AppIntentResult> OpenUriAsync(Uri uri, string? preferredAppId = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This intents gateway does not support opening URIs.");

    /// <summary>
    /// Requests an app's settings (ADR 0096, section 4): the app itself with <c>--show-settings</c>
    /// when it declares the intent, otherwise the app's page in Settings (<c>--app=&lt;id&gt;</c>).
    /// </summary>
    /// <exception cref="AppGatewayAccessDeniedException">Policy denies <see cref="AppCapabilities.AppsLaunch"/>.</exception>
    ValueTask<AppIntentResult> ShowAppSettingsAsync(string appId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This intents gateway does not support showing app settings.");

    /// <summary>
    /// Sends an app-defined intent (ADR 0096, section 5) to the installed apps declaring
    /// <paramref name="intentId"/>: the only one, the remembered choice, or
    /// <see cref="AppIntentOutcome.ChooserRequired"/> with the candidates.
    /// </summary>
    /// <param name="intentId">Well-formed identifier, <c>domain.reversed.name.vN</c>.</param>
    /// <param name="payloadJson">Optional JSON object of at most 64 KiB.</param>
    /// <param name="targetAppId">The app to deliver to, when the caller already chose one.</param>
    /// <param name="rememberChoice">With <paramref name="targetAppId"/>: "always use" this app for this intent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="AppGatewayAccessDeniedException">Policy denies <see cref="AppCapabilities.AppsLaunch"/>.</exception>
    ValueTask<AppIntentResult> SendIntentAsync(
        string intentId,
        string? payloadJson,
        string? targetAppId = null,
        bool rememberChoice = false,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This intents gateway does not support app-defined intents.");
}

/// <summary>Identifies the stable outcome of one <see cref="IAppServiceControlGateway"/> request.</summary>
public enum ServiceControlOutcome
{
    /// <summary>The request completed: the service was started/stopped, or its start mode was read/set.</summary>
    Succeeded,

    /// <summary>No catalog app matches the requested target ID.</summary>
    NotFound,

    /// <summary>The resolved target app is not a <see cref="AppKind.Service"/> app.</summary>
    NotAService,

    /// <summary>The target service's start mode is <see cref="ServiceStartMode.Disabled"/>; it cannot be started.</summary>
    ServiceDisabled,

    /// <summary>The target's entry point faulted while starting.</summary>
    Faulted
}

/// <summary>Contains the result of one <see cref="IAppServiceControlGateway"/> request.</summary>
/// <param name="Outcome">Stable request outcome.</param>
/// <param name="ErrorCode">Stable machine-readable error code when the outcome did not succeed.</param>
public sealed record ServiceControlResult(ServiceControlOutcome Outcome, string? ErrorCode = null);

/// <summary>
/// Provides one app instance's authorized ability to start, stop, or reconfigure the start mode
/// of a <see cref="AppKind.Service"/> app other than itself. Always permitted between apps whose
/// entry points are compiled into the same assembly (a companion Window/Terminal app controlling
/// its own bundled background service); any other caller needs
/// <see cref="AppCapabilities.ServicesManage"/> (a "service manager" role, e.g. System Monitor).
/// </summary>
public interface IAppServiceControlGateway
{
    /// <summary>
    /// Starts the target service, or focuses it if it is already running. Refused when the
    /// service's effective start mode is <see cref="ServiceStartMode.Disabled"/>.
    /// </summary>
    /// <exception cref="AppGatewayAccessDeniedException">
    /// Policy denies control of this service: the caller is neither in the same assembly nor
    /// holds <see cref="AppCapabilities.ServicesManage"/>.
    /// </exception>
    ValueTask<ServiceControlResult> StartAsync(string serviceAppId, CancellationToken cancellationToken = default);

    /// <summary>Stops the target service if it is currently running.</summary>
    /// <exception cref="AppGatewayAccessDeniedException">
    /// Policy denies control of this service: the caller is neither in the same assembly nor
    /// holds <see cref="AppCapabilities.ServicesManage"/>.
    /// </exception>
    ValueTask<ServiceControlResult> StopAsync(string serviceAppId, CancellationToken cancellationToken = default);

    /// <summary>Reads the target service's effective start mode.</summary>
    /// <exception cref="AppGatewayAccessDeniedException">
    /// Policy denies control of this service: the caller is neither in the same assembly nor
    /// holds <see cref="AppCapabilities.ServicesManage"/>.
    /// </exception>
    ValueTask<ServiceStartMode> GetStartModeAsync(string serviceAppId, CancellationToken cancellationToken = default);

    /// <summary>Sets the target service's effective start mode.</summary>
    /// <exception cref="AppGatewayAccessDeniedException">
    /// Policy denies control of this service: the caller is neither in the same assembly nor
    /// holds <see cref="AppCapabilities.ServicesManage"/>.
    /// </exception>
    ValueTask<ServiceControlResult> SetStartModeAsync(
        string serviceAppId, ServiceStartMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the target service's activation state, last result and health (ADR 0097). Needs no
    /// capability: like <c>systemctl status</c>, any user may read any unit's state.
    /// </summary>
    /// <param name="serviceAppId">Exact catalog app ID of the service.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The snapshot; <see cref="ServiceStatusSnapshot.NeverStarted"/> for an unknown or never-started service.</returns>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0097.</exception>
    ValueTask<ServiceStatusSnapshot> GetStatusAsync(string serviceAppId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This service-control gateway does not report service states.");

    /// <summary>
    /// Clears the <see cref="ServiceActiveState.Failed"/> state of the target service, like
    /// <c>systemctl reset-failed</c>. Same authorization rule as <see cref="StartAsync"/>.
    /// </summary>
    /// <param name="serviceAppId">Exact catalog app ID of the service.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="AppGatewayAccessDeniedException">Policy denies control of this service.</exception>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0097.</exception>
    ValueTask<ServiceControlResult> ResetFailedAsync(string serviceAppId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This service-control gateway does not report service states.");
}
