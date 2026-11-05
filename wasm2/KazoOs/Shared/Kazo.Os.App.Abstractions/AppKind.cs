using System.Text.Json.Serialization;

namespace Kazo.Os.App.Abstractions;

/// <summary>
/// Identifies the lifecycle and hosting model used by an application.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppKind>))]
public enum AppKind
{
    /// <summary>An interactive Blazor application hosted by the window manager.</summary>
    [JsonStringEnumMemberName("window")]
    Window,

    /// <summary>A command executable hosted by a terminal session.</summary>
    [JsonStringEnumMemberName("terminal")]
    Terminal,

    /// <summary>A non-visual application that runs for the active OS session.</summary>
    [JsonStringEnumMemberName("service")]
    Service,

    /// <summary>
    /// A web site served by the OS (ADR 0073): a non-visual process that listens on the URLs its
    /// manifest declares (<see cref="AppManifest.Web"/>), started on demand by the first request
    /// to one of them (ADR 0074, section 3), and run in the shared web worker.
    /// </summary>
    [JsonStringEnumMemberName("web")]
    Web
}