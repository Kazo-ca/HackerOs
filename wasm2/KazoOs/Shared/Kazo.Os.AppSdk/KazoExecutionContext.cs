using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.AppSdk;

/// <summary>
/// Carries the app identity (and working directory) of the code currently running, so the
/// <c>Kazo.Os.AppSdk.IO</c> facade (<c>KazoFile</c>, <c>KazoDirectory</c>, ...) can reach the
/// caller's gateways without the developer passing an <see cref="IAppExecutionContext"/> around
/// (ADR 0043, section 2).
/// </summary>
/// <remarks>
/// <para>
/// The value lives in an <see cref="AsyncLocal{T}"/>, which flows across <c>await</c>,
/// <c>Task.Run</c> and <see cref="System.Threading.Timer"/> because all of them capture the
/// <see cref="ExecutionContext"/>. It does <b>not</b> flow into JavaScript callbacks
/// (<c>[JSInvokable]</c>): such a method must call <see cref="Enter"/> itself with its own
/// <c>AppContext</c>.
/// </para>
/// <para>
/// The OS opens a scope at every entry point where app code starts running (terminal command,
/// service run/stop, window component lifecycle and events, gateway callbacks). There is
/// deliberately no fallback: code running outside any scope gets a
/// <see cref="KazoExecutionContextMissingException"/> instead of silently borrowing a system or
/// anonymous identity.
/// </para>
/// <para>
/// <see cref="Enter"/> is public on purpose: authority comes from the gateways, which only the
/// platform's trusted factory builds. An app that fabricates its own context only ever reaches its
/// own fake gateways, so entering it grants nothing.
/// </para>
/// </remarks>
public static class KazoExecutionContext
{
    private static readonly Lock HookGate = new();

    // Not readonly: the resource-accounting hook (ADR 0066, 0067) swaps in an AsyncLocal built with
    // a value-changed callback, once, at service composition, before any scope exists. Without the
    // hook this stays an ordinary AsyncLocal with no added cost.
    private static AsyncLocal<KazoAmbientFrame?> s_currentFrame = new();
    private static IExecutionContextSwitchObserver? s_switchObserver;
    private static IExecutionModeSwitchObserver? s_modeObserver;
    private static int s_liveScopes;

    /// <summary>Gets the context of the app running the current code.</summary>
    /// <exception cref="KazoExecutionContextMissingException">No scope is active.</exception>
    public static IAppExecutionContext Current => RequireFrame().App;

    /// <summary>Gets the context of the app running the current code, or <see langword="null"/> outside any scope.</summary>
    public static IAppExecutionContext? CurrentOrNull => s_currentFrame.Value?.App;

    /// <summary>
    /// Gets the canonical absolute working directory of the code currently running: the working
    /// directory of its process (ADR 0050, section 6), unless the scope was opened with an explicit
    /// one that no <see cref="KazoEnvironment.SetCurrentDirectoryAsync"/> has replaced since.
    /// </summary>
    /// <exception cref="KazoExecutionContextMissingException">No scope is active.</exception>
    public static string WorkingDirectory => RequireFrame().WorkingDirectory;

    /// <summary>
    /// Makes <paramref name="context"/> the ambient app context until the returned scope is
    /// disposed, which restores whatever was ambient before (scopes nest).
    /// </summary>
    /// <param name="context">The app context to make ambient.</param>
    /// <param name="workingDirectory">
    /// Absolute working directory pinned for this scope; <see langword="null"/> follows the
    /// process's own working directory (<see cref="IAppExecutionContext.Environment"/>), which is
    /// the home directory for a process started from the desktop.
    /// </param>
    /// <returns>A scope whose disposal restores the previous ambient context.</returns>
    /// <exception cref="ArgumentException"><paramref name="workingDirectory"/> is not a valid absolute path.</exception>
    public static IDisposable Enter(IAppExecutionContext context, string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? directory = workingDirectory is null ? null : Canonicalize(workingDirectory, nameof(workingDirectory));
        KazoAmbientFrame? previous = s_currentFrame.Value;
        Interlocked.Increment(ref s_liveScopes);
        s_currentFrame.Value = new KazoAmbientFrame(context, directory);
        return new Scope(previous);
    }

    /// <summary>Gets whether the context-switch hook is installed (ADR 0066: never when every measurement is off).</summary>
    internal static bool IsContextSwitchHookInstalled => Volatile.Read(ref s_switchObserver) is not null;

    /// <summary>
    /// Installs the common context-switch hook of the resource measurements (ADR 0067, section 1):
    /// the ambient frame becomes an <see cref="AsyncLocal{T}"/> whose value-changed callback reports
    /// every change of running process to <paramref name="observer"/>, including the restores the
    /// runtime makes at the start and end of each continuation.
    /// </summary>
    /// <param name="observer">The single observer; a dispatcher fans it out to the measurements.</param>
    /// <exception cref="InvalidOperationException">
    /// A hook is already installed, or a scope is open: swapping the <see cref="AsyncLocal{T}"/>
    /// would make every flow that captured the old one lose its app context, so the hook is only
    /// installed at service composition, before any app code runs.
    /// </exception>
    /// <remarks>Reserved to <c>Kazo.Os.Platform.ResourceAccounting</c> (internals visible to it only).</remarks>
    internal static void InstallContextSwitchHook(IExecutionContextSwitchObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (HookGate)
        {
            if (s_switchObserver is not null)
            {
                throw new InvalidOperationException("The context-switch hook is already installed.");
            }

            ThrowIfScopesOpen();
            Volatile.Write(ref s_modeObserver, observer as IExecutionModeSwitchObserver);
            Volatile.Write(ref s_switchObserver, observer);
            s_currentFrame = new AsyncLocal<KazoAmbientFrame?>(OnFrameChanged);
        }
    }

    /// <summary>
    /// Removes the hook and restores an ordinary <see cref="AsyncLocal{T}"/>. Same rule as
    /// <see cref="InstallContextSwitchHook"/>: no scope may be open. Used by tests and host shutdown.
    /// </summary>
    internal static void UninstallContextSwitchHook()
    {
        lock (HookGate)
        {
            if (s_switchObserver is null)
            {
                return;
            }

            ThrowIfScopesOpen();
            s_currentFrame = new AsyncLocal<KazoAmbientFrame?>();
            Volatile.Write(ref s_switchObserver, null);
            Volatile.Write(ref s_modeObserver, null);
        }
    }

    /// <summary>
    /// Opens the "kernel" sub-frame of the running process around an OS gateway call (ADR 0067,
    /// section 2): the time spent until the scope is disposed, including the continuations of the
    /// asynchronous work started inside it, is the process's <c>stime</c> rather than its <c>utime</c>.
    /// </summary>
    /// <returns>
    /// A scope restoring the user frame on disposal. It does nothing (and costs nothing) when the
    /// context-switch hook is not installed (CPU and memory measurement off, ADR 0066), outside any
    /// process frame (OS code), or when the process is already in kernel mode (nested gateways).
    /// </returns>
    /// <remarks>
    /// Reserved to the OS (internals visible to Platform Core): an app marking its own code as kernel
    /// time would only falsify its own <c>/proc/&lt;pid&gt;/stat</c>, but the gateways are the only honest place.
    /// </remarks>
    internal static KernelScope EnterKernel()
    {
        if (Volatile.Read(ref s_switchObserver) is null || s_currentFrame.Value is not { IsKernel: false } frame)
        {
            return default;
        }

        Interlocked.Increment(ref s_liveScopes);
        s_currentFrame.Value = new KazoAmbientFrame(frame);
        return new KernelScope(frame);
    }

    private static void ThrowIfScopesOpen()
    {
        if (Volatile.Read(ref s_liveScopes) != 0)
        {
            throw new InvalidOperationException(
                "The context-switch hook can only change while no KazoExecutionContext scope is open " +
                "(install it at service composition, before any app code runs).");
        }
    }

    /// <summary>
    /// Value-changed callback of the hooked frame: the "context switch" of ADR 0067. It runs inside
    /// the runtime's ExecutionContext restore, where an exception would tear the process down, so it
    /// never throws; it reports only actual changes of process.
    /// </summary>
    private static void OnFrameChanged(AsyncLocalValueChangedArgs<KazoAmbientFrame?> args)
    {
        try
        {
            KazoAmbientFrame? previous = args.PreviousValue;
            KazoAmbientFrame? current = args.CurrentValue;
            ProcessId? from = previous?.App.ProcessId;
            ProcessId? to = current?.App.ProcessId;
            bool fromKernel = previous?.IsKernel == true;
            bool toKernel = current?.IsKernel == true;
            if (from != to)
            {
                Volatile.Read(ref s_switchObserver)?.OnContextSwitch(
                    new ExecutionContextSwitch(from, to, args.ThreadContextChanged, fromKernel, toKernel));
            }
            else if (fromKernel != toKernel && to is { } process)
            {
                // Same process, other mode: entering or leaving an OS gateway (ADR 0067, section 2).
                Volatile.Read(ref s_modeObserver)?.OnModeSwitch(process, toKernel, args.ThreadContextChanged);
            }
        }
        catch (Exception)
        {
            // Deliberately swallowed: see the summary. A lost measurement beats a crashed OS.
        }
    }

    /// <summary>
    /// Drops the working directory pinned by <see cref="Enter"/> on the current scope, so it follows
    /// the process's directory again. Called after the process changed directory.
    /// </summary>
    internal static void UnpinWorkingDirectory()
    {
        if (s_currentFrame.Value is { } frame)
        {
            (frame.UserFrame ?? frame).PinnedDirectory = null;
        }
    }

    private static KazoAmbientFrame RequireFrame() =>
        s_currentFrame.Value ?? throw new KazoExecutionContextMissingException();

    private static string Canonicalize(string path, string parameterName)
    {
        try
        {
            return VirtualPath.Parse(path).Value;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new ArgumentException($"'{path}' is not a valid absolute working directory.", parameterName, exception);
        }
    }

    /// <summary>
    /// One ambient frame: the running app and an optional pinned working directory. The frame is
    /// shared by every flow that inherited it, so unpinning it is seen by all of them, like a
    /// process-wide <c>chdir</c>.
    /// </summary>
    private sealed class KazoAmbientFrame
    {
        public KazoAmbientFrame(IAppExecutionContext app, string? pinnedDirectory)
        {
            App = app;
            PinnedDirectory = pinnedDirectory;
        }

        /// <summary>Builds the kernel sub-frame of <paramref name="userFrame"/> (ADR 0067): same process, same directory.</summary>
        public KazoAmbientFrame(KazoAmbientFrame userFrame)
        {
            App = userFrame.App;
            UserFrame = userFrame;
        }

        public IAppExecutionContext App { get; }

        /// <summary>Gets the user frame this kernel sub-frame belongs to; <see langword="null"/> for a user frame.</summary>
        public KazoAmbientFrame? UserFrame { get; }

        /// <summary>Gets whether this frame is the process running inside an OS gateway (its time is <c>stime</c>).</summary>
        public bool IsKernel => UserFrame is not null;

        public string? PinnedDirectory { get; set; }

        // The process table only ever stores canonical paths, so no re-parse is needed. A kernel
        // sub-frame follows its user frame, so a chdir seen by one is seen by the other.
        public string WorkingDirectory => UserFrame?.WorkingDirectory ?? PinnedDirectory ?? App.Environment.CurrentDirectory;
    }

    /// <summary>
    /// Scope of <see cref="EnterKernel"/>: a struct, so an OS gateway call allocates nothing for it.
    /// The default value (hook off, no process) does nothing.
    /// </summary>
    internal readonly struct KernelScope : IDisposable
    {
        private readonly KazoAmbientFrame? _userFrame;

        public KernelScope(object userFrame) => _userFrame = (KazoAmbientFrame)userFrame;

        /// <summary>Restores the user frame. Dispose it once, with <c>using</c>.</summary>
        public void Dispose()
        {
            if (_userFrame is not null)
            {
                s_currentFrame.Value = _userFrame;
                Interlocked.Decrement(ref s_liveScopes);
            }
        }
    }

    /// <summary>Restores the previous frame exactly once.</summary>
    private sealed class Scope(KazoAmbientFrame? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                s_currentFrame.Value = previous;
                Interlocked.Decrement(ref s_liveScopes);
            }
        }
    }
}

/// <summary>
/// Thrown when code that needs the ambient app context (the <c>Hack*</c> file API, for instance)
/// runs outside any <see cref="KazoExecutionContext.Enter"/> scope.
/// </summary>
public sealed class KazoExecutionContextMissingException : InvalidOperationException
{
    /// <summary>Initializes the exception with the standard explanatory message.</summary>
    public KazoExecutionContextMissingException()
        : base("No Kazo OS app context is active on this execution flow. App code normally runs inside a scope " +
               "opened by the OS; a [JSInvokable] callback or a thread started outside the app must call " +
               "KazoExecutionContext.Enter(AppContext) itself.")
    {
    }
}
