using System.Globalization;
using Kazo.Os.AppSdk.Diagnostics;

namespace Kazo.Os.Apps.Diagnostic;

/// <summary>
/// Text of the isolation badge's tooltip (ADR 0146): the status read by
/// <see cref="KazoCrossOriginIsolation"/>, written with the keys and values of the first lines of
/// <c>/proc/kazoos/workers</c>, so the tooltip and <c>cat</c> say the same thing.
/// </summary>
internal static class IsolationStatus
{
    /// <summary>The short reason, as <c>/proc/kazoos/workers</c> writes it.</summary>
    /// <param name="reason">The reason.</param>
    public static string Format(KazoCrossOriginIsolationReason reason) => reason switch
    {
        KazoCrossOriginIsolationReason.None => "-",
        KazoCrossOriginIsolationReason.Setting => "setting",
        KazoCrossOriginIsolationReason.MissingHeaders => "missing-headers",
        KazoCrossOriginIsolationReason.InsecureContext => "insecure-context",
        KazoCrossOriginIsolationReason.Browser => "browser",
        _ => "not-browser"
    };

    /// <summary>Every <c>key: value</c> line, in the order of <c>/proc/kazoos/workers</c>.</summary>
    /// <param name="status">The page's isolation.</param>
    public static string Describe(KazoCrossOriginIsolationStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        string[] lines =
        [
            $"cross-origin-isolated: {YesNo(status.IsIsolated)}",
            $"isolation-reason: {Format(status.Reason)}",
            $"isolation-setting: {status.Setting.ToString().ToLowerInvariant()}",
            $"embedder-policy: {status.EmbedderPolicy ?? "-"}",
            $"shared-pipes: {YesNo(status.SharedPipes)}",
            $"sync-calls: {YesNo(status.SynchronousWorkerCalls)}",
            $"memory-measure: {(status.BrowserMemoryMeasurement ? "browser" : "estimated")}",
            $"timer-resolution-us: {status.TimerResolution.TotalMicroseconds.ToString("0.##", CultureInfo.InvariantCulture)}"
        ];
        return string.Join('\n', lines);

        static string YesNo(bool value) => value ? "yes" : "no";
    }
}
