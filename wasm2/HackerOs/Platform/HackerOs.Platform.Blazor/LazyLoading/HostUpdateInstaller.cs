namespace HackerOs.Platform.Blazor.LazyLoading;

/// <summary>What downloading the version the origin serves gave (ADR 0223).</summary>
public enum HostUpdateDownloadResult
{
    /// <summary>No service worker runs this page (local development, server without one): a plain reload takes the served files.</summary>
    NoWorker,

    /// <summary>The running worker is the one the origin serves: a reload already takes its version.</summary>
    UpToDate,

    /// <summary>The new worker downloaded and checked the new version; it waits for its activation.</summary>
    Ready,

    /// <summary>The new version could not be downloaded (offline, deployment in progress, integrity failure): the current version stays.</summary>
    Unavailable,
}

/// <summary>
/// The host's service worker, seen from the OS (ADR 0223): downloads the version the origin serves
/// into a waiting worker, then activates it. The browser host implements it with
/// <c>hostUpdate.js</c>; tests use a fake.
/// </summary>
public interface IHostUpdateWorker
{
    /// <summary>
    /// Asks the browser to check the served worker script and waits until a new worker, if any,
    /// has downloaded its version.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    ValueTask<HostUpdateDownloadResult> DownloadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Activates the waiting worker (<c>SKIP_WAITING</c>) and waits until it controls the page.
    /// The page is not reloaded: the caller restarts the machine.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns><see langword="true"/> when the new worker controls the page.</returns>
    ValueTask<bool> ActivateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Starts listening for a new worker activated by another tab of this site; the page then has
    /// to restart, since its files belong to the previous version.
    /// </summary>
    /// <param name="activatedElsewhere">Called once per activation by another tab.</param>
    /// <param name="cancellationToken">Cancels the registration.</param>
    ValueTask ListenAsync(Action activatedElsewhere, CancellationToken cancellationToken);
}

/// <summary>
/// Makes « Redémarrer pour mettre à jour » land on the new version (ADR 0223). The previous
/// service worker serves every file of its own version, page included, and a reload keeps it in
/// control; so the OS downloads the new version in the background as soon as it is announced
/// (ADR 0174, ADR 0162), activates the new worker once the session has ended, and only then
/// reloads. Another tab that activated it makes this one restart cleanly too.
/// </summary>
public sealed class HostUpdateInstaller
{
    private readonly IHostUpdateWorker _worker;
    private readonly object _gate = new();
    private Task<HostUpdateDownloadResult>? _download;

    /// <summary>Creates the installer.</summary>
    /// <param name="worker">The host's service worker.</param>
    public HostUpdateInstaller(IHostUpdateWorker worker) =>
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));

    /// <summary>Raised when another tab activated the new version: this page must restart.</summary>
    public event Action? ActivatedElsewhere;

    /// <summary>Listens for an activation by another tab; called once at boot.</summary>
    /// <param name="cancellationToken">Cancels the registration.</param>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _worker.ListenAsync(() => ActivatedElsewhere?.Invoke(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Without the listener, another tab's update reloads this one the old way (index.html).
        }
    }

    /// <summary>
    /// Downloads the served version. Calls made while a download runs share it; a finished
    /// download that found something to install is kept, any other result is checked again at the
    /// next call. Never throws, except on cancellation.
    /// </summary>
    /// <param name="cancellationToken">Cancels this caller's wait, not the shared download.</param>
    public Task<HostUpdateDownloadResult> DownloadAsync(CancellationToken cancellationToken = default)
    {
        Task<HostUpdateDownloadResult> download;
        lock (_gate)
        {
            if (_download is null
                || (_download.IsCompleted && (!_download.IsCompletedSuccessfully || _download.Result != HostUpdateDownloadResult.Ready)))
            {
                _download = RunDownloadAsync();
            }

            download = _download;
        }

        return download.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Activates the downloaded version, if there is one: the next page load then comes from the
    /// new worker. Called once the session has ended, just before the reload.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns><see langword="true"/> when a new worker took control of the page.</returns>
    public async Task<bool> ActivateDownloadedAsync(CancellationToken cancellationToken = default)
    {
        if (await DownloadAsync(cancellationToken).ConfigureAwait(false) != HostUpdateDownloadResult.Ready)
        {
            return false;
        }

        try
        {
            return await _worker.ActivateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<HostUpdateDownloadResult> RunDownloadAsync()
    {
        try
        {
            return await _worker.DownloadAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A JS failure is a download that did not happen; the current version keeps running.
            return HostUpdateDownloadResult.Unavailable;
        }
    }
}
