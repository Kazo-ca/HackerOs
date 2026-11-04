namespace HackerOs.AppSdk.Diagnostics;

/// <summary>The <c>HackerOs:CrossOriginIsolation</c> setting of the host (ADR 0131, section 1).</summary>
public enum HackCrossOriginIsolationSetting
{
    /// <summary>On when this build passed the isolation audit, off otherwise. The default.</summary>
    Auto,

    /// <summary>The host always sends the isolation headers.</summary>
    On,

    /// <summary>The host sends no isolation header.</summary>
    Off
}

/// <summary>Why the page is not cross-origin isolated (ADR 0131, section 5).</summary>
public enum HackCrossOriginIsolationReason
{
    /// <summary>The page is isolated.</summary>
    None,

    /// <summary>The setting resolves to off.</summary>
    Setting,

    /// <summary>The setting wants isolation, but the headers did not reach the page.</summary>
    MissingHeaders,

    /// <summary>The page is not a secure context.</summary>
    InsecureContext,

    /// <summary>The headers arrived, but the browser does not isolate the page.</summary>
    Browser,

    /// <summary>The OS does not run in a browser (tests, tools).</summary>
    NotBrowser
}

/// <summary>
/// The page's cross-origin isolation as the OS decided it at boot (ADR 0131; ADR 0146), the same
/// facts as the first lines of <c>/proc/hackeros/workers</c>.
/// </summary>
/// <param name="IsIsolated">The page's <c>crossOriginIsolated</c>.</param>
/// <param name="Reason">Why it is not, or <see cref="HackCrossOriginIsolationReason.None"/>.</param>
/// <param name="Setting">The host's setting.</param>
/// <param name="EmbedderPolicy">The COEP value in effect (<c>credentialless</c>, <c>require-corp</c>), or <see langword="null"/>.</param>
/// <param name="SharedPipes">Whether the workers carry their messages in shared rings.</param>
/// <param name="SynchronousWorkerCalls">Whether a process in a worker may call the OS synchronously.</param>
/// <param name="BrowserMemoryMeasurement">Whether the browser measures the workers' memory.</param>
/// <param name="TimerResolution">The measured resolution of the page's clock.</param>
public sealed record HackCrossOriginIsolationStatus(
    bool IsIsolated,
    HackCrossOriginIsolationReason Reason,
    HackCrossOriginIsolationSetting Setting,
    string? EmbedderPolicy,
    bool SharedPipes,
    bool SynchronousWorkerCalls,
    bool BrowserMemoryMeasurement,
    TimeSpan TimerResolution);

/// <summary>
/// Reads the OS's cross-origin isolation, behind <see cref="HackCrossOriginIsolation"/>. Reached through
/// <see cref="IAppExecutionContext.CrossOriginIsolation"/>. Needs no capability (ADR 0146).
/// </summary>
public interface IAppCrossOriginIsolationGateway
{
    /// <summary>Gets the isolation the OS decided at boot.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask<HackCrossOriginIsolationStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Whether the page that runs Kazo OS is cross-origin isolated, and what that turns on (ADR 0131,
/// ADR 0146). Like <c>uname</c> or <c>/proc/cpuinfo</c> under Linux, it describes the machine, not
/// another app, so no capability is needed; <c>/proc/hackeros/workers</c> holds the same facts for
/// apps that may read <c>/proc</c>.
/// </summary>
/// <example>
/// <code>
/// HackCrossOriginIsolationStatus isolation = await HackCrossOriginIsolation.GetStatusAsync();
/// Console.WriteLine(isolation.IsIsolated ? "isolated" : $"not isolated: {isolation.Reason}");
/// </code>
/// </example>
public static class HackCrossOriginIsolation
{
    /// <summary>Gets the isolation the OS decided at boot; it does not change until the page reloads.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="HackExecutionContextMissingException">Called outside an app's execution context.</exception>
    /// <exception cref="NotSupportedException">The execution context does not provide this information.</exception>
    public static ValueTask<HackCrossOriginIsolationStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        HackExecutionContext.Current.CrossOriginIsolation.GetStatusAsync(cancellationToken);
}
