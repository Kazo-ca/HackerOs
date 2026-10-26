using System.Text.Json;

namespace HackerOs.Platform.Blazor.LazyLoading;

/// <summary>
/// Reads the version of HackerOS the origin serves now (ADR 0174): the <c>version.json</c> file the
/// build publishes next to the page. The browser host fetches it past every cache.
/// </summary>
public interface IHostReleaseProbe
{
    /// <summary>Gets the content of the served <c>version.json</c>, or <see langword="null"/> when unreachable.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask<string?> GetServedVersionDocumentAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Checks every few minutes whether the origin serves another version of HackerOS than the one
/// running (ADR 0174), like an update notifier: it only says a newer release waits; the host shows
/// it and offers the restart. Each check also asks <see cref="HostBuildUpdateMonitor"/> whether the
/// build changed under the page with the same version (ADR 0162).
/// </summary>
public sealed class HostReleaseMonitor : IAsyncDisposable
{
    /// <summary>The default time between two checks: five minutes.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    private readonly IHostReleaseProbe _probe;
    private readonly HostBuildUpdateMonitor? _buildMonitor;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;
    private string? _availableVersion;

    /// <summary>Creates the monitor.</summary>
    /// <param name="probe">Reads the served <c>version.json</c>.</param>
    /// <param name="runningVersion">The version of the running OS (<c>HackerOsSdk.Version</c>).</param>
    /// <param name="buildMonitor">Also checked at each tick, when given.</param>
    public HostReleaseMonitor(IHostReleaseProbe probe, string runningVersion, HostBuildUpdateMonitor? buildMonitor = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        ArgumentException.ThrowIfNullOrWhiteSpace(runningVersion);
        RunningVersion = runningVersion;
        _buildMonitor = buildMonitor;
    }

    /// <summary>Gets the version of the running OS.</summary>
    public string RunningVersion { get; }

    /// <summary>Gets the other version the origin serves, or <see langword="null"/> while none was found.</summary>
    public string? AvailableVersion => Volatile.Read(ref _availableVersion);

    /// <summary>Raised each time the origin is found serving a version not reported yet.</summary>
    public event Action<string>? ReleaseAvailable;

    /// <summary>
    /// Reads the served version once. An unreachable origin or an unreadable file is not an
    /// update: the OS keeps running offline.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns><see langword="true"/> when the origin serves another version than the running one.</returns>
    public async Task<bool> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_buildMonitor is not null)
        {
            await _buildMonitor.CheckAsync(cancellationToken).ConfigureAwait(false);
        }

        string? served;
        try
        {
            served = ParseVersion(await _probe.GetServedVersionDocumentAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }

        if (served is null || string.Equals(served, RunningVersion, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(Interlocked.Exchange(ref _availableVersion, served), served, StringComparison.Ordinal))
        {
            ReleaseAvailable?.Invoke(served);
        }

        return true;
    }

    /// <summary>Starts checking at <paramref name="interval"/>, the first check after one interval. A second call does nothing.</summary>
    /// <param name="interval">Time between two checks; <see cref="DefaultInterval"/> in the browser host.</param>
    public void Start(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _loop ??= RunAsync(interval, _stopping.Token);
    }

    /// <summary>
    /// Returns the <c>version</c> of a <c>version.json</c> document, or <see langword="null"/> when
    /// the document is missing, malformed or has no version.
    /// </summary>
    /// <param name="document">The JSON text: <c>{ "product": "HackerOS", "version": "2.1.0" }</c>.</param>
    public static string? ParseVersion(string? document)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            return null;
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(document);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("version", out JsonElement version)
                && version.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(version.GetString())
                ? version.GetString()!.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopping is the expected end of the loop.
            }
        }

        _stopping.Dispose();
    }

    private async Task RunAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await CheckAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
