using System.Text.Json.Serialization;

namespace Kazo.Os.App.Abstractions;

/// <summary>
/// When the OS starts an automatic <see cref="AppKind.Service"/> app (ADR 0097, section 4), like
/// systemd's system and user managers: a session service starts after a login and stops at the
/// logout; a system service starts at boot, before the login screen, and stops only at shutdown.
/// </summary>
/// <remarks>
/// Only a system app (ADR 0048, <c>IsSystemApp</c> on the host's reference) may run at boot: for any
/// other app the catalog reads <see cref="Boot"/> as <see cref="Login"/> and writes a warning to
/// <c>/var/log/syslog</c>.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<AppServiceStartup>))]
public enum AppServiceStartup
{
    /// <summary>After a login, as the logged-in user; stopped at the logout (the default).</summary>
    [JsonStringEnumMemberName("login")]
    Login = 0,

    /// <summary>At boot, before the login screen, as the service account; stopped only at shutdown.</summary>
    [JsonStringEnumMemberName("boot")]
    Boot = 1
}
