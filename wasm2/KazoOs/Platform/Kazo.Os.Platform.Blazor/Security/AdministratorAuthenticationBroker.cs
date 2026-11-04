using Kazo.Os.Platform.Core.Elevation;

namespace Kazo.Os.Platform.Blazor.Security;

/// <summary>
/// The host's <see cref="IAdministratorAuthenticationPrompt"/> (ADR 0055, section 5): queues the
/// authentication agent's requests for the <see cref="AdministratorAuthenticationOverlay"/> the
/// platform shell renders, as <see cref="DangerousCapabilityApprovalBroker"/> does for ADR 0049.
/// </summary>
/// <remarks>
/// With no attached overlay (no shell yet, or a headless host), a request ends at once as
/// <see cref="AdministratorAuthenticationOutcome.Unavailable"/>. Requests are shown one at a time,
/// in arrival order. When the shell goes away, pending requests end as cancelled.
/// </remarks>
public sealed class AdministratorAuthenticationBroker : IAdministratorAuthenticationPrompt
{
    private readonly Lock _gate = new();
    private readonly List<PendingAuthentication> _queue = [];
    private int _attachedHosts;

    /// <summary>Raised when <see cref="Current"/> changes; the overlay re-renders.</summary>
    public event Action? Changed;

    /// <summary>Gets the request the overlay must show, or <see langword="null"/> when none.</summary>
    public PendingAuthentication? Current
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
    public Task<AdministratorAuthenticationOutcome> RequestAsync(
        AdministratorAuthenticationPromptRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        PendingAuthentication pending;
        lock (_gate)
        {
            if (_attachedHosts == 0)
            {
                return Task.FromResult(AdministratorAuthenticationOutcome.Unavailable);
            }

            pending = new PendingAuthentication(request, this);
            _queue.Add(pending);
        }

        if (cancellationToken.CanBeCanceled)
        {
            CancellationTokenRegistration registration = cancellationToken.Register(
                () => pending.Complete(AdministratorAuthenticationOutcome.Cancelled));
            _ = pending.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        }

        Changed?.Invoke();
        return pending.Task;
    }

    private void Remove(PendingAuthentication pending)
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
        PendingAuthentication[] orphaned = [];
        lock (_gate)
        {
            _attachedHosts = Math.Max(0, _attachedHosts - 1);
            if (_attachedHosts == 0)
            {
                orphaned = [.. _queue];
            }
        }

        // Nobody can answer any more: the operation is refused, nothing is written.
        foreach (PendingAuthentication pending in orphaned)
        {
            pending.Complete(AdministratorAuthenticationOutcome.Cancelled);
        }
    }

    /// <summary>One request waiting for the overlay.</summary>
    public sealed class PendingAuthentication
    {
        private readonly TaskCompletionSource<AdministratorAuthenticationOutcome> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AdministratorAuthenticationBroker _owner;

        internal PendingAuthentication(AdministratorAuthenticationPromptRequest request, AdministratorAuthenticationBroker owner)
        {
            Request = request;
            _owner = owner;
        }

        /// <summary>Gets what to show and how to submit credentials.</summary>
        public AdministratorAuthenticationPromptRequest Request { get; }

        internal Task<AdministratorAuthenticationOutcome> Task => _completion.Task;

        /// <summary>Ends the request and removes it from the screen.</summary>
        /// <param name="outcome">How the prompt ended.</param>
        public void Complete(AdministratorAuthenticationOutcome outcome)
        {
            if (_completion.TrySetResult(outcome))
            {
                _owner.Remove(this);
            }
        }
    }

    private sealed class Attachment(AdministratorAuthenticationBroker owner) : IDisposable
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
