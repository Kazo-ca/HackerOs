namespace Kazo.Os.Server.Services;

/// <summary>
/// Logs the relay policy once at startup (ADR 0102 §5): one warning per setting that weakens the
/// defaults, with a reminder that this server is single-user (ADR 0027), so whoever signs in to it
/// gets the relay as configured.
/// </summary>
/// <param name="policy">The effective relay policy.</param>
/// <param name="logger">The server log.</param>
public sealed partial class ProxyPolicyStartupReporter(ProxyRelayPolicy policy, ILogger<ProxyPolicyStartupReporter> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!policy.Enabled)
        {
            LogDisabled(logger);
            return Task.CompletedTask;
        }

        IReadOnlyList<string> weakenings = policy.Weakenings();
        foreach (string warning in weakenings)
        {
            LogWeakening(logger, warning);
        }

        if (weakenings.Count > 0)
        {
            LogSingleUser(logger);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 1020, Level = LogLevel.Information, Message = "Network relay disabled (KazoOs:Proxy:Enabled is false): apps have no route to the real network.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(EventId = 1021, Level = LogLevel.Warning, Message = "Network relay: {Warning}")]
    private static partial void LogWeakening(ILogger logger, string warning);

    [LoggerMessage(EventId = 1022, Level = LogLevel.Warning, Message = "Network relay: this server is single-user (ADR 0027); every account signed in to it gets the relay with these settings.")]
    private static partial void LogSingleUser(ILogger logger);
}
