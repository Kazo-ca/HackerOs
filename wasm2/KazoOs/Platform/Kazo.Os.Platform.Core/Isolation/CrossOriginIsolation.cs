using System.Text.Json;

namespace Kazo.Os.Platform.Core.Isolation;

/// <summary>
/// The <c>KazoOs:CrossOriginIsolation</c> setting of a host (ADR 0131, section 1).
/// </summary>
public enum CrossOriginIsolationMode
{
    /// <summary>On when the audit of ADR 0131 section 3 passed for this build (<see cref="CrossOriginIsolationPolicy.AuditPassed"/>), off otherwise. The default.</summary>
    Auto,

    /// <summary>The headers are always sent.</summary>
    On,

    /// <summary>No header is sent, and the service worker adds none.</summary>
    Off
}

/// <summary>
/// Why the page is not cross-origin isolated, shown by <c>/proc/kazoos/workers</c> and Diagnostic
/// (ADR 0131, section 5).
/// </summary>
public enum CrossOriginIsolationReason
{
    /// <summary>The page is isolated.</summary>
    None,

    /// <summary>The setting resolves to off (<c>off</c>, or <c>auto</c> with the audit not passed).</summary>
    Setting,

    /// <summary>The setting wants isolation but the headers did not reach the page (no service worker yet, lost by a proxy).</summary>
    MissingHeaders,

    /// <summary>The page is not a secure context (plain <c>http</c> on a non-local address).</summary>
    InsecureContext,

    /// <summary>The headers arrived but the browser does not isolate the page (no support, or neither COEP value accepted).</summary>
    Browser,

    /// <summary>Not running in a browser (unit tests, the loopback workers).</summary>
    NotBrowser
}

/// <summary>
/// The header policy of ADR 0131 shared by every place that sends the headers: the PWA's service
/// worker (which mirrors <see cref="AuditPassed"/> in <c>service-worker.published.js</c>), the
/// middleware of <c>test/test</c> and <c>Kazo.Os.Server</c>, and the page's own status.
/// </summary>
public static class CrossOriginIsolationPolicy
{
    /// <summary>The configuration key of the setting (<c>auto</c>, <c>on</c> or <c>off</c>).</summary>
    public const string SettingKey = "KazoOs:CrossOriginIsolation";

    /// <summary>
    /// Whether the audit of ADR 0131 section 3 passed for this version, which turns <c>auto</c> on.
    /// Written by <c>exec/9-cross-origin-isolation</c> with its report in <c>worker-processes.md</c>;
    /// <c>service-worker.published.js</c> keeps the same value (checked by a unit test).
    /// </summary>
    public const bool AuditPassed = true;

    /// <summary>The <c>Cross-Origin-Opener-Policy</c> header.</summary>
    public const string OpenerPolicyHeader = "Cross-Origin-Opener-Policy";

    /// <summary>The <c>Cross-Origin-Embedder-Policy</c> header.</summary>
    public const string EmbedderPolicyHeader = "Cross-Origin-Embedder-Policy";

    /// <summary>The opener policy value: the page keeps its own browsing context group.</summary>
    public const string OpenerPolicy = "same-origin";

    /// <summary>
    /// The embedder policy value: a third-party resource without CORS loads without cookies instead
    /// of being blocked (ADR 0131, section 2).
    /// </summary>
    public const string EmbedderPolicy = "credentialless";

    /// <summary>The embedder policy for a browser that does not know <see cref="EmbedderPolicy"/>.</summary>
    public const string StrictEmbedderPolicy = "require-corp";

    /// <summary>Reads the setting; a missing or unknown value is <see cref="CrossOriginIsolationMode.Auto"/>.</summary>
    /// <param name="value">The text of <see cref="SettingKey"/>, or <see langword="null"/>.</param>
    public static CrossOriginIsolationMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "on" or "true" => CrossOriginIsolationMode.On,
        "off" or "false" => CrossOriginIsolationMode.Off,
        _ => CrossOriginIsolationMode.Auto
    };

    /// <summary>Gets whether a host with <paramref name="mode"/> sends the headers.</summary>
    /// <param name="mode">The setting.</param>
    /// <param name="auditPassed">The audit result; <see cref="AuditPassed"/> unless a test says otherwise.</param>
    public static bool SendsHeaders(CrossOriginIsolationMode mode, bool auditPassed = AuditPassed) => mode switch
    {
        CrossOriginIsolationMode.On => true,
        CrossOriginIsolationMode.Off => false,
        _ => auditPassed
    };

    /// <summary>
    /// The headers a server host (<c>test/test</c>, <c>Kazo.Os.Server</c>) adds to every response for
    /// <paramref name="mode"/>: none when it resolves to off (ADR 0131, section 2).
    /// </summary>
    /// <param name="mode">The host's setting.</param>
    public static IReadOnlyList<KeyValuePair<string, string>> ServerHeaders(CrossOriginIsolationMode mode) =>
        SendsHeaders(mode)
            ? [new(OpenerPolicyHeader, OpenerPolicy), new(EmbedderPolicyHeader, EmbedderPolicy)]
            : [];

    /// <summary>The setting's text, as written in <c>appsettings.json</c>.</summary>
    /// <param name="mode">The setting.</param>
    public static string Format(CrossOriginIsolationMode mode) => mode switch
    {
        CrossOriginIsolationMode.On => "on",
        CrossOriginIsolationMode.Off => "off",
        _ => "auto"
    };

    /// <summary>The short text of a reason, as <c>/proc/kazoos/workers</c> prints it.</summary>
    /// <param name="reason">The reason.</param>
    public static string Format(CrossOriginIsolationReason reason) => reason switch
    {
        CrossOriginIsolationReason.None => "-",
        CrossOriginIsolationReason.Setting => "setting",
        CrossOriginIsolationReason.MissingHeaders => "missing-headers",
        CrossOriginIsolationReason.InsecureContext => "insecure-context",
        CrossOriginIsolationReason.Browser => "browser",
        _ => "not-browser"
    };
}

/// <summary>
/// What the page found at start (ADR 0131, sections 1 and 5): the OS decides on
/// <c>crossOriginIsolated</c> in the page, never on the setting alone.
/// </summary>
/// <param name="Mode">The host's setting.</param>
/// <param name="IsIsolated">The page's <c>crossOriginIsolated</c>.</param>
/// <param name="Reason">Why it is not, or <see cref="CrossOriginIsolationReason.None"/>.</param>
/// <param name="EmbedderPolicy">The COEP value in effect (<c>credentialless</c>, <c>require-corp</c>), or <see langword="null"/>.</param>
/// <param name="SharedPipes">Whether workers carry their messages in shared rings (section 4).</param>
/// <param name="SynchronousCalls">Whether a worker may call the OS synchronously (section 4).</param>
/// <param name="MemoryMeasurement">Whether <c>performance.measureUserAgentSpecificMemory()</c> measures the workers.</param>
/// <param name="TimerResolution">The measured resolution of <c>performance.now()</c>.</param>
public sealed record CrossOriginIsolationStatus(
    CrossOriginIsolationMode Mode,
    bool IsIsolated,
    CrossOriginIsolationReason Reason,
    string? EmbedderPolicy,
    bool SharedPipes,
    bool SynchronousCalls,
    bool MemoryMeasurement,
    TimeSpan TimerResolution)
{
    /// <summary>The status outside a browser: nothing isolated, nothing shared.</summary>
    public static CrossOriginIsolationStatus NotBrowser { get; } = new(
        CrossOriginIsolationMode.Auto, false, CrossOriginIsolationReason.NotBrowser, null, false, false, false, TimeSpan.Zero);
}

/// <summary>
/// The page's cross-origin isolation, decided once at start (ADR 0131): the browser host sets
/// <see cref="Status"/> from what the page found; <c>/proc/kazoos/workers</c>, the worker pool
/// and the memory accountant read it. Until then, and outside a browser, it is
/// <see cref="CrossOriginIsolationStatus.NotBrowser"/>.
/// </summary>
public sealed class CrossOriginIsolationState
{
    private CrossOriginIsolationStatus _status = CrossOriginIsolationStatus.NotBrowser;

    /// <summary>Gets or sets what the page found.</summary>
    public CrossOriginIsolationStatus Status
    {
        get => Volatile.Read(ref _status);
        set => Volatile.Write(ref _status, value ?? throw new ArgumentNullException(nameof(value)));
    }
}

/// <summary>
/// Runs the host's probe once at boot and records its result in <see cref="CrossOriginIsolationState"/>
/// (ADR 0131): registered by the browser host over <c>BrowserCrossOriginIsolation</c>, absent elsewhere.
/// </summary>
public sealed class CrossOriginIsolationProbe
{
    private readonly CrossOriginIsolationState _state;
    private readonly Func<Task<CrossOriginIsolationStatus>> _probe;

    /// <summary>Initializes the probe.</summary>
    /// <param name="state">Where the result goes.</param>
    /// <param name="probe">Asks the page.</param>
    public CrossOriginIsolationProbe(CrossOriginIsolationState state, Func<Task<CrossOriginIsolationStatus>> probe)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    /// <summary>Asks the page, records and returns the result.</summary>
    public async Task<CrossOriginIsolationStatus> RunAsync()
    {
        CrossOriginIsolationStatus status = await _probe().ConfigureAwait(false);
        _state.Status = status;
        return status;
    }

    /// <summary>
    /// Turns the page's answer (the JSON of <c>crossOriginIsolation.js</c>'s <c>probe()</c>) into a
    /// status; <see langword="null"/> (the probe could not run) reads as a browser that does not isolate.
    /// </summary>
    /// <param name="mode">The host's setting.</param>
    /// <param name="json">What the page answered.</param>
    public static CrossOriginIsolationStatus Read(CrossOriginIsolationMode mode, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return CrossOriginIsolationStatus.NotBrowser with { Mode = mode, Reason = CrossOriginIsolationReason.Browser };
        }

        bool wanted = CrossOriginIsolationPolicy.SendsHeaders(mode);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        bool isolated = Flag(root, "isolated");
        bool workerSends = Flag(root, "workerSends");
        string? embedder = root.TryGetProperty("workerEmbedderPolicy", out JsonElement policy) && policy.ValueKind == JsonValueKind.String
            ? policy.GetString()
            : null;

        // Isolated, else in order: no secure context, the setting says no, the service worker sent
        // the headers and the browser ignored them, or they never arrived.
        CrossOriginIsolationReason reason = isolated ? CrossOriginIsolationReason.None
            : !Flag(root, "secureContext") ? CrossOriginIsolationReason.InsecureContext
            : !wanted && !workerSends ? CrossOriginIsolationReason.Setting
            : workerSends ? CrossOriginIsolationReason.Browser
            : CrossOriginIsolationReason.MissingHeaders;
        double microseconds = root.TryGetProperty("timerResolutionUs", out JsonElement us) && us.ValueKind == JsonValueKind.Number ? us.GetDouble() : 0;
        bool shared = Flag(root, "sharedArrayBuffer");
        return new CrossOriginIsolationStatus(
            mode,
            isolated,
            reason,
            isolated && workerSends ? embedder : null,
            SharedPipes: shared,
            SynchronousCalls: shared,
            MemoryMeasurement: Flag(root, "measureMemory"),
            TimeSpan.FromTicks((long)Math.Round(microseconds * TimeSpan.TicksPerMicrosecond)));

        static bool Flag(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
    }

    /// <summary>The boot line of <c>/var/log/syslog</c>, like a kernel parameter in <c>dmesg</c>.</summary>
    /// <param name="status">What the page found.</param>
    public static string Describe(CrossOriginIsolationStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        string setting = CrossOriginIsolationPolicy.Format(status.Mode);
        string timer = $"timers {status.TimerResolution.TotalMicroseconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} us";
        return status.IsIsolated
            ? $"isolation: cross-origin isolated (setting {setting}, COEP {status.EmbedderPolicy ?? "set by the server"}): "
                + $"shared pipes {(status.SharedPipes ? "on" : "off")}, synchronous worker calls {(status.SynchronousCalls ? "on" : "off")}, "
                + $"worker memory {(status.MemoryMeasurement ? "measured by the browser" : "estimated")}, {timer}"
            : $"isolation: not cross-origin isolated ({CrossOriginIsolationPolicy.Format(status.Reason)}, setting {setting}): "
                + $"workers use messages, synchronous worker calls refused, worker memory estimated, {timer}";
    }
}
