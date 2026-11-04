using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.ServiceProcess;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Systemctl;

/// <summary>
/// <c>systemctl [list-units|status|start|stop|restart|enable|disable|mask|is-active|is-enabled|is-failed|reset-failed] [UNIT...]</c>
/// (ADR 0064, ADR 0097): the Kazo OS services (the catalog's <c>Service</c> apps, through
/// <see cref="KazoServiceController"/>) shown and driven as systemd units. A unit is named by its
/// app id or short name, with or without <c>.service</c> (<c>sync</c>, <c>sync.service</c>,
/// <c>org.hackeros.sync</c>). Start types map to systemd states: automatic = <c>enabled</c>,
/// manual = <c>disabled</c>, disabled = <c>masked</c>. Changing another app's service needs
/// <c>services.manage</c>. Also answers as <c>service NAME start</c>. The active state is the
/// service manager's (ADR 0097): a service that crashed reads as <c>failed (Result: exit-code)</c>
/// until it is started again or <c>systemctl reset-failed</c> clears it.
/// </summary>
public sealed class SystemctlCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Messages follow the process language; tables, states and the status block stay English, as systemd's (ADR 0104, section 5).
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        List<string> args = [.. context.Arguments.Where(a => a is not ("--no-pager" or "--user" or "--system" or "-q" or "--quiet" or "--now" or "--all" or "-a"))];
        IReadOnlyList<string> argv = context.App.Environment.GetCommandLineArgs();
        if (argv.Count > 0 && argv[0] == "service" && args.Count >= 2)
        {
            // `service NAME ACTION` is `systemctl ACTION NAME`.
            args = [args[1], args[0], .. args.Skip(2)];
        }

        string verb = args.Count == 0 ? "list-units" : args[0];
        List<string> units = [.. args.Skip(1)];
        KazoServiceController[] services;
        try
        {
            services = await KazoServiceController.GetServicesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or NotSupportedException or IOException)
        {
            await context.StandardError.WriteLineAsync(text["BusFailed", exception.Message]);
            return 1;
        }

        if (verb is "list-units" or "list-unit-files" or "list")
        {
            return await ListAsync(context, text, services, verb == "list-unit-files");
        }

        if (units.Count == 0 && verb == "reset-failed")
        {
            // Without a unit, systemd resets every failed unit.
            units = [.. services.Where(static s => s.ActiveState == KazoServiceActiveState.Failed).Select(static s => s.ServiceName)];
            if (units.Count == 0)
            {
                return 0;
            }
        }

        if (units.Count == 0)
        {
            await context.StandardError.WriteLineAsync(text["TooFewArguments"]);
            return 1;
        }

        int status = 0;
        foreach (string unit in units)
        {
            KazoServiceController? service = Find(services, unit);
            if (service is null)
            {
                if (verb is "is-active" or "is-failed")
                {
                    // systemd prints the state of an unknown unit rather than an error.
                    await context.StandardOutput.WriteLineAsync("inactive");
                    status = Math.Max(status, verb == "is-active" ? 3 : 1);
                    continue;
                }

                await context.StandardError.WriteLineAsync(verb is "status" or "is-enabled"
                    ? text["UnitNotFound", UnitName(unit)]
                    : text["VerbUnitNotFound", verb, UnitName(unit)]);
                status = verb is "status" ? 4 : 5;
                continue;
            }

            try
            {
                status = Math.Max(status, await RunAsync(context, text, verb, service, cancellationToken));
            }
            catch (UnauthorizedAccessException)
            {
                await context.StandardError.WriteLineAsync(text["AccessDenied", verb, UnitName(service)]);
                await context.StandardError.WriteLineAsync(text["AccessDeniedHint"]);
                status = Math.Max(status, 4);
            }
            catch (InvalidOperationException exception)
            {
                await context.StandardError.WriteLineAsync(text["VerbFailed", verb, UnitName(service), exception.Message]);
                status = Math.Max(status, 1);
            }
        }

        return status;
    }

    private static async Task<int> RunAsync(TerminalExecutionContext context, IStringLocalizer<Strings> text, string verb, KazoServiceController service, CancellationToken cancellationToken)
    {
        switch (verb)
        {
            case "start":
                await service.StartAsync(cancellationToken);
                return 0;
            case "stop":
                await service.StopAsync(cancellationToken);
                return 0;
            case "restart" or "reload" or "try-restart":
                await service.StopAsync(cancellationToken);
                await service.StartAsync(cancellationToken);
                return 0;
            case "enable":
                await service.SetStartTypeAsync(ServiceStartMode.Automatic, cancellationToken);
                await context.StandardOutput.WriteLineAsync(text["CreatedSymlink", $"/etc/systemd/system/multi-user.target.wants/{UnitName(service)}", $"/usr/lib/systemd/system/{UnitName(service)}"]);
                return 0;
            case "disable":
                await service.SetStartTypeAsync(ServiceStartMode.Manual, cancellationToken);
                await context.StandardOutput.WriteLineAsync(text["Removed", $"/etc/systemd/system/multi-user.target.wants/{UnitName(service)}"]);
                return 0;
            case "mask":
                await service.SetStartTypeAsync(ServiceStartMode.Disabled, cancellationToken);
                await context.StandardOutput.WriteLineAsync(text["CreatedSymlink", $"/etc/systemd/system/{UnitName(service)}", "/dev/null"]);
                return 0;
            case "unmask":
                await service.SetStartTypeAsync(ServiceStartMode.Manual, cancellationToken);
                await context.StandardOutput.WriteLineAsync(text["Removed", $"/etc/systemd/system/{UnitName(service)}"]);
                return 0;
            case "is-active":
                // systemd prints the ActiveState and exits 0 only for active (3 for failed too).
                await context.StandardOutput.WriteLineAsync(Active(service).Active);
                return service.ActiveState == KazoServiceActiveState.Active ? 0 : 3;
            case "is-failed":
                await context.StandardOutput.WriteLineAsync(Active(service).Active);
                return service.ActiveState == KazoServiceActiveState.Failed ? 0 : 1;
            case "reset-failed":
                await service.ResetFailedAsync(cancellationToken);
                return 0;
            case "is-enabled":
                await context.StandardOutput.WriteLineAsync(Enabled(service));
                return service.StartType == ServiceStartMode.Automatic ? 0 : 1;
            case "status":
                await WriteStatusAsync(context, service);
                return service.ActiveState == KazoServiceActiveState.Active ? 0 : 3;
            default:
                await context.StandardError.WriteLineAsync(text["UnknownVerb", verb]);
                return 1;
        }
    }

    /// <summary>
    /// Prints a unit like <c>systemctl status</c> (ADR 0097, section 3): the dot (green ● active,
    /// red × failed, ○ otherwise), the active state with its sub-state or result and since when,
    /// the main PID, and the service's own status line and health when it reported them.
    /// </summary>
    private static async Task WriteStatusAsync(TerminalExecutionContext context, KazoServiceController service)
    {
        TextWriter output = context.StandardOutput;
        (string active, string sub) = Active(service);
        string dot = service.ActiveState switch
        {
            KazoServiceActiveState.Active => "\u001b[32m●\u001b[0m",
            KazoServiceActiveState.Failed => "\u001b[31m×\u001b[0m",
            KazoServiceActiveState.Activating or KazoServiceActiveState.Deactivating => "\u001b[1m●\u001b[0m",
            _ => "○"
        };
        string activeText = service.ActiveState == KazoServiceActiveState.Failed
            ? $"\u001b[31m{active}\u001b[0m ({sub})"
            : service.ActiveState == KazoServiceActiveState.Active ? $"\u001b[32m{active} ({sub})\u001b[0m" : $"{active} ({sub})";
        if (service.ActiveStateChangedAt is { } since)
        {
            activeText += $" since {since:ddd yyyy-MM-dd HH:mm:ss} UTC; {Ago(context.App.Clock.UtcNow - since)} ago";
        }

        await output.WriteLineAsync($"{dot} {UnitName(service)} - {service.DisplayName}");
        await output.WriteLineAsync($"     Loaded: loaded (/usr/lib/systemd/system/{UnitName(service)}; {Enabled(service)}; preset: enabled)");
        await output.WriteLineAsync($"     Active: {activeText}");
        if (service.ProcessId is { } pid)
        {
            await output.WriteLineAsync($"   Main PID: {pid} ({ShortName(service.ServiceName)})");
        }
        else if (service.ActiveState == KazoServiceActiveState.Failed && service.ExitCode is { } exitCode)
        {
            await output.WriteLineAsync($"    Process: (code=exited, status={exitCode})");
        }

        if (service.StatusText is { Length: > 0 } text)
        {
            await output.WriteLineAsync($"     Status: \"{text}\"");
        }

        if (service.Health is KazoServiceHealth.Degraded or KazoServiceHealth.Unhealthy)
        {
            await output.WriteLineAsync($"     Health: {service.Health.ToString().ToLowerInvariant()}");
        }

        await output.WriteLineAsync($"       Docs: {service.Description}");
    }

    /// <summary>Formats a duration the way systemd does after "since …;" (<c>3min 12s</c>, <c>2h 5min</c>).</summary>
    private static string Ago(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return elapsed.TotalSeconds < 60 ? $"{(int)elapsed.TotalSeconds}s"
            : elapsed.TotalMinutes < 60 ? $"{(int)elapsed.TotalMinutes}min {elapsed.Seconds}s"
            : elapsed.TotalHours < 24 ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}min"
            : $"{(int)elapsed.TotalDays} days";
    }

    private static async Task<int> ListAsync(TerminalExecutionContext context, IStringLocalizer<Strings> text, KazoServiceController[] services, bool files)
    {
        if (files)
        {
            await context.StandardOutput.WriteLineAsync($"{"UNIT FILE",-40} {"STATE",-9} PRESET");
            foreach (KazoServiceController service in services)
            {
                await context.StandardOutput.WriteLineAsync($"{UnitName(service),-40} {Enabled(service),-9} enabled");
            }

            await context.StandardOutput.WriteLineAsync();
            await context.StandardOutput.WriteLineAsync(text["UnitFilesListed", services.Length]);
            return 0;
        }

        await context.StandardOutput.WriteLineAsync($"  {"UNIT",-40} {"LOAD",-6} {"ACTIVE",-8} {"SUB",-8} DESCRIPTION");
        foreach (KazoServiceController service in services)
        {
            (string active, string sub) = Active(service);
            string marker = service.ActiveState == KazoServiceActiveState.Failed ? "●" : " ";
            sub = service.ActiveState == KazoServiceActiveState.Failed ? "failed" : sub;
            await context.StandardOutput.WriteLineAsync($"{marker} {UnitName(service),-40} {"loaded",-6} {active,-8} {sub,-8} {service.DisplayName}");
        }

        await context.StandardOutput.WriteLineAsync();
        await context.StandardOutput.WriteLineAsync(text["Legend.Load"]);
        await context.StandardOutput.WriteLineAsync(text["Legend.Active"]);
        await context.StandardOutput.WriteLineAsync(text["Legend.Sub"]);
        await context.StandardOutput.WriteLineAsync(text["UnitsListed", services.Length]);
        return 0;
    }

    /// <summary>The ACTIVE and SUB columns; for a failed unit, SUB is <c>Result: …</c> in <c>status</c> and <c>failed</c> in lists.</summary>
    private static (string Active, string Sub) Active(KazoServiceController service) => service.ActiveState switch
    {
        KazoServiceActiveState.Active => ("active", "running"),
        KazoServiceActiveState.Activating => ("activating", "start"),
        KazoServiceActiveState.Deactivating => ("deactivating", "stop-sigterm"),
        KazoServiceActiveState.Failed => ("failed", $"Result: {KazoServiceStates.ToSystemdName(service.Result)}"),
        _ => ("inactive", "dead")
    };

    private static string Enabled(KazoServiceController service) => service.StartType switch
    {
        ServiceStartMode.Automatic => "enabled",
        ServiceStartMode.Manual => "disabled",
        ServiceStartMode.Disabled => "masked",
        _ => "static"
    };

    private static KazoServiceController? Find(KazoServiceController[] services, string unit)
    {
        string name = unit.EndsWith(".service", StringComparison.Ordinal) ? unit[..^8] : unit;
        return services.FirstOrDefault(s => string.Equals(s.ServiceName, name, StringComparison.OrdinalIgnoreCase))
            ?? services.FirstOrDefault(s => string.Equals(ShortName(s.ServiceName), name, StringComparison.OrdinalIgnoreCase));
    }

    private static string ShortName(string appId) => appId[(appId.LastIndexOf('.') + 1)..];

    private static string UnitName(KazoServiceController service) => ShortName(service.ServiceName) + ".service";

    private static string UnitName(string unit) => unit.EndsWith(".service", StringComparison.Ordinal) ? unit : unit + ".service";
}
