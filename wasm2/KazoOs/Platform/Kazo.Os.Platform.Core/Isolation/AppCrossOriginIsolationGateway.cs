using Kazo.Os.AppSdk.Diagnostics;

namespace Kazo.Os.Platform.Core.Isolation;

/// <summary>
/// Trusted <see cref="IAppCrossOriginIsolationGateway"/> behind <see cref="HackCrossOriginIsolation"/>
/// (ADR 0146): the status the boot probe recorded in <see cref="CrossOriginIsolationState"/>, the same
/// one <c>/proc/hackeros/workers</c> prints. No capability is checked: like <c>uname</c>, it describes
/// the machine, not another app.
/// </summary>
public sealed class AppCrossOriginIsolationGateway : IAppCrossOriginIsolationGateway
{
    private readonly Func<CrossOriginIsolationStatus?> _status;

    /// <summary>Initializes the gateway.</summary>
    /// <param name="status">Reads the current status lazily; <see langword="null"/> reads as not in a browser.</param>
    public AppCrossOriginIsolationGateway(Func<CrossOriginIsolationStatus?> status) =>
        _status = status ?? throw new ArgumentNullException(nameof(status));

    /// <inheritdoc />
    public ValueTask<HackCrossOriginIsolationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ToSdk(_status() ?? CrossOriginIsolationStatus.NotBrowser));
    }

    /// <summary>Maps the platform's status to the SDK's record.</summary>
    /// <param name="status">What the page found at boot.</param>
    public static HackCrossOriginIsolationStatus ToSdk(CrossOriginIsolationStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new HackCrossOriginIsolationStatus(
            status.IsIsolated,
            status.Reason switch
            {
                CrossOriginIsolationReason.None => HackCrossOriginIsolationReason.None,
                CrossOriginIsolationReason.Setting => HackCrossOriginIsolationReason.Setting,
                CrossOriginIsolationReason.MissingHeaders => HackCrossOriginIsolationReason.MissingHeaders,
                CrossOriginIsolationReason.InsecureContext => HackCrossOriginIsolationReason.InsecureContext,
                CrossOriginIsolationReason.Browser => HackCrossOriginIsolationReason.Browser,
                _ => HackCrossOriginIsolationReason.NotBrowser
            },
            status.Mode switch
            {
                CrossOriginIsolationMode.On => HackCrossOriginIsolationSetting.On,
                CrossOriginIsolationMode.Off => HackCrossOriginIsolationSetting.Off,
                _ => HackCrossOriginIsolationSetting.Auto
            },
            status.EmbedderPolicy,
            status.SharedPipes,
            status.SynchronousCalls,
            status.MemoryMeasurement,
            status.TimerResolution);
    }
}
