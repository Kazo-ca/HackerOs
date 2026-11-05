namespace Kazo.Os.HostSmoke.Tests;

/// <summary>
/// What <c>build/verify-hosts.sh</c> tells the suite about the host under test, through
/// environment variables (the only channel <c>dotnet test</c> passes through unchanged).
/// </summary>
/// <param name="Url">Base address of the running host, e.g. <c>http://127.0.0.1:5123</c>.</param>
/// <param name="HostName">Label used in the output (<c>ecosystem</c>, <c>test-host</c>, <c>server</c>…).</param>
/// <param name="ExpectedCpuSource">
/// When set, the value <c>/proc/self/status</c> must show on its <c>CpuSource:</c> line
/// (ADR 0066), e.g. <c>simulated</c> for a run with the measurement switched off.
/// </param>
/// <param name="ExpectedMemSource">Same as <paramref name="ExpectedCpuSource"/> for <c>MemSource:</c>.</param>
/// <param name="ArtifactsDirectory">Where a failing run leaves its screenshot, or <see langword="null"/>.</param>
/// <param name="LimitsEnabled">
/// Whether the host applies the process limits of ADR 0125 (<c>KazoOs:ResourceLimits:Enabled</c>);
/// <see langword="false"/> for the run that switches them off.
/// </param>
/// <param name="ExpectsOomKill">
/// Whether the run turns the OOM killer on and expects <c>stress --vm</c> to be killed (ADR 0126):
/// the <c>ecosystem-oom</c> run, whose simulated <c>MemTotal</c> is small.
/// </param>
/// <param name="ExpectedIsolation">
/// When set, whether the page must be cross-origin isolated (ADR 0131): <see langword="true"/> for a
/// host that sends COOP/COEP (its service worker or its server), <see langword="false"/> for a run
/// with <c>KazoOs:CrossOriginIsolation</c> off; <see langword="null"/> checks nothing.
/// </param>
public sealed record HostSmokeSettings(
    string Url,
    string HostName,
    string? ExpectedCpuSource,
    string? ExpectedMemSource,
    string? ArtifactsDirectory,
    bool LimitsEnabled = true,
    bool ExpectsOomKill = false,
    bool? ExpectedIsolation = null)
{
    /// <summary>Base address of the host under test. Required.</summary>
    public const string UrlVariable = "KAZOOS_SMOKE_URL";

    /// <summary>Label of the host under test.</summary>
    public const string HostVariable = "KAZOOS_SMOKE_HOST";

    /// <summary>Expected <c>CpuSource:</c> value.</summary>
    public const string CpuSourceVariable = "KAZOOS_SMOKE_EXPECT_CPU_SOURCE";

    /// <summary>Expected <c>MemSource:</c> value.</summary>
    public const string MemSourceVariable = "KAZOOS_SMOKE_EXPECT_MEM_SOURCE";

    /// <summary>Directory for failure screenshots.</summary>
    public const string ArtifactsVariable = "KAZOOS_SMOKE_ARTIFACTS";

    /// <summary><c>off</c> when the host switches the process limits off (ADR 0125).</summary>
    public const string LimitsVariable = "KAZOOS_SMOKE_EXPECT_LIMITS";

    /// <summary><c>on</c> when the run checks the OOM killer (ADR 0126).</summary>
    public const string OomVariable = "KAZOOS_SMOKE_EXPECT_OOM";

    /// <summary><c>yes</c> or <c>no</c>: the page's expected <c>crossOriginIsolated</c> (ADR 0131).</summary>
    public const string IsolationVariable = "KAZOOS_SMOKE_EXPECT_ISOLATION";

    /// <summary>Reads the settings, or returns <see langword="null"/> when no URL is set.</summary>
    /// <summary>
    /// Gets whether the host runs commands in Web Workers (ADR 0070): every host since ADR 0130, the
    /// server included, runs the OS in WebAssembly.
    /// </summary>
    public bool ExpectsWorkers => true;

    /// <summary>
    /// Gets whether the OS must connect by itself to the host that served it (ADR 0134): only the
    /// <c>server</c> run is a Kazo OS server.
    /// </summary>
    public bool ExpectsServerConnection => string.Equals(HostName, "server", StringComparison.Ordinal);

    public static HostSmokeSettings? FromEnvironment()
    {
        string? url = Read(UrlVariable);
        if (url is null)
        {
            return null;
        }

        return new HostSmokeSettings(
            url.TrimEnd('/'),
            Read(HostVariable) ?? "host",
            Read(CpuSourceVariable),
            Read(MemSourceVariable),
            Read(ArtifactsVariable),
            !string.Equals(Read(LimitsVariable), "off", StringComparison.Ordinal),
            string.Equals(Read(OomVariable), "on", StringComparison.Ordinal),
            Read(IsolationVariable) switch
            {
                "yes" => true,
                "no" => false,
                _ => null
            });
    }

    private static string? Read(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
