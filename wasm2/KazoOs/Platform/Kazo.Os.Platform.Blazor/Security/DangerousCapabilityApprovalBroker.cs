using Kazo.Os.Platform.Core.Policy;

namespace Kazo.Os.Platform.Blazor.Security;

/// <summary>
/// The host's <see cref="IDangerousCapabilityApprovalPrompt"/> (ADR 0049 section 6): queues approval
/// requests for the <see cref="DangerousCapabilityApprovalOverlay"/> rendered by the platform shell.
/// </summary>
/// <remarks>
/// The overlay attaches itself when the desktop or mobile shell renders it. With no attached overlay
/// (no interactive session, for instance before any shell exists) a request ends immediately as
/// <see cref="DangerousCapabilityApprovalOutcome.Unavailable"/>, which the gate logs as
/// <c>capability.deferred</c>. Requests are shown one at a time, in arrival order: only one secure
/// overlay can own the screen.
/// </remarks>
public sealed class DangerousCapabilityApprovalBroker : IDangerousCapabilityApprovalPrompt
{
    private readonly Lock _gate = new();
    private readonly Queue<PendingApproval> _queue = new();
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
                return _queue.Count > 0 ? _queue.Peek() : null;
            }
        }
    }

    /// <summary>Gets whether an overlay is attached and can show a request.</summary>
    public bool HasHost
    {
        get
        {
            lock (_gate)
            {
                return _attachedHosts > 0;
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
    public Task<DangerousCapabilityApprovalOutcome> RequestAsync(
        DangerousCapabilityApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        PendingApproval pending;
        lock (_gate)
        {
            if (_attachedHosts == 0)
            {
                return Task.FromResult(DangerousCapabilityApprovalOutcome.Unavailable);
            }

            pending = new PendingApproval(request, this);
            _queue.Enqueue(pending);
        }

        if (cancellationToken.CanBeCanceled)
        {
            CancellationTokenRegistration registration = cancellationToken.Register(
                () => pending.Complete(DangerousCapabilityApprovalOutcome.Dismissed));
            _ = pending.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        }

        Changed?.Invoke();
        return pending.Task;
    }

    private void Remove(PendingApproval pending)
    {
        bool changed;
        lock (_gate)
        {
            changed = _queue.Count > 0 && ReferenceEquals(_queue.Peek(), pending);
            if (_queue.Contains(pending))
            {
                PendingApproval[] remaining = [.. _queue.Where(item => !ReferenceEquals(item, pending))];
                _queue.Clear();
                foreach (PendingApproval item in remaining)
                {
                    _queue.Enqueue(item);
                }

                changed = true;
            }
        }

        if (changed)
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

        // The shell went away (logout, shell switch): nobody can answer, so nothing is written.
        foreach (PendingApproval pending in orphaned)
        {
            pending.Complete(DangerousCapabilityApprovalOutcome.Dismissed);
        }
    }

    /// <summary>One request waiting for the overlay.</summary>
    public sealed class PendingApproval
    {
        private readonly TaskCompletionSource<DangerousCapabilityApprovalOutcome> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly DangerousCapabilityApprovalBroker _owner;

        internal PendingApproval(DangerousCapabilityApprovalRequest request, DangerousCapabilityApprovalBroker owner)
        {
            Request = request;
            _owner = owner;
        }

        /// <summary>Gets what to show and how to submit credentials.</summary>
        public DangerousCapabilityApprovalRequest Request { get; }

        internal Task<DangerousCapabilityApprovalOutcome> Task => _completion.Task;

        /// <summary>Ends the request with the user's choice and removes it from the screen.</summary>
        /// <param name="outcome">How the prompt ended.</param>
        public void Complete(DangerousCapabilityApprovalOutcome outcome)
        {
            if (_completion.TrySetResult(outcome))
            {
                _owner.Remove(this);
            }
        }
    }

    private sealed class Attachment(DangerousCapabilityApprovalBroker owner) : IDisposable
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
