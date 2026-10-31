using HackerOs.Platform.Core.Elevation;

namespace HackerOs.Platform.Blazor.Security;

/// <summary>
/// The host's <see cref="IAppInstallApprovalPrompt"/> (ADR 0189 §4, step 2): queues the approval
/// requests of app installs for the <see cref="AppInstallApprovalOverlay"/> the platform shell
/// renders, as <see cref="AdministratorAuthenticationBroker"/> does for the sign-in of step 1.
/// </summary>
/// <remarks>
/// With no attached overlay (no shell yet, or a headless host), a request ends at once as
/// <see cref="AppInstallApprovalOutcome.Unavailable"/> and nothing is installed. Requests are shown
/// one at a time, in arrival order. When the shell goes away, pending requests end as refused.
/// </remarks>
public sealed class AppInstallApprovalBroker : IAppInstallApprovalPrompt
{
    private readonly Lock _gate = new();
    private readonly List<PendingApproval> _queue = [];
    private int _attachedHosts;

    /// <summary>Raised when <see cref="Current"/> changes; the overlay re-renders.</summary>
    public event Action? Changed;

    /// <summary>Gets the request the overlay must show, or <see langword="null"/> when none.</summary>
    public PendingApproval? Current
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count > 0 ? _queue[0] : null;
            }
        }
    }

    /// <summary>Registers a rendered overlay; dispose the result when it is removed.</summary>
    public IDisposable Attach()
    {
        lock (_gate)
        {
            _attachedHosts++;
        }

        return new Attachment(this);
    }

    /// <inheritdoc />
    public Task<AppInstallApprovalOutcome> RequestAsync(AppInstallApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        PendingApproval pending;
        lock (_gate)
        {
            if (_attachedHosts == 0)
            {
                return Task.FromResult(AppInstallApprovalOutcome.Unavailable);
            }

            pending = new PendingApproval(request, this);
            _queue.Add(pending);
        }

        if (cancellationToken.CanBeCanceled)
        {
            CancellationTokenRegistration registration = cancellationToken.Register(
                () => pending.Complete(AppInstallApprovalOutcome.Refused));
            _ = pending.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        }

        Changed?.Invoke();
        return pending.Task;
    }

    private void Remove(PendingApproval pending)
    {
        bool removed;
        lock (_gate)
        {
            removed = _queue.Remove(pending);
        }

        if (removed)
        {
            Changed?.Invoke();
        }
    }

    private void Detach()
    {
        PendingApproval[] orphaned = [];
        lock (_gate)
        {
            _attachedHosts = Math.Max(0, _attachedHosts - 1);
            if (_attachedHosts == 0)
            {
                orphaned = [.. _queue];
            }
        }

        // Nobody can answer any more: the install is refused, nothing is written.
        foreach (PendingApproval pending in orphaned)
        {
            pending.Complete(AppInstallApprovalOutcome.Refused);
        }
    }

    /// <summary>One request waiting for the overlay.</summary>
    public sealed class PendingApproval
    {
        private readonly TaskCompletionSource<AppInstallApprovalOutcome> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AppInstallApprovalBroker _owner;

        internal PendingApproval(AppInstallApprovalRequest request, AppInstallApprovalBroker owner)
        {
            Request = request;
            _owner = owner;
        }

        /// <summary>Gets what to show.</summary>
        public AppInstallApprovalRequest Request { get; }

        internal Task<AppInstallApprovalOutcome> Task => _completion.Task;

        /// <summary>Ends the request and removes it from the screen.</summary>
        /// <param name="outcome">How the window ended.</param>
        public void Complete(AppInstallApprovalOutcome outcome)
        {
            if (_completion.TrySetResult(outcome))
            {
                _owner.Remove(this);
            }
        }
    }

    private sealed class Attachment(AppInstallApprovalBroker owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Detach();
            }
        }
    }
}
