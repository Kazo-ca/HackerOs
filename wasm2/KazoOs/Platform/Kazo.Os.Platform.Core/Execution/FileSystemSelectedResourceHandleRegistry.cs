using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Time;

namespace Kazo.Os.Platform.Core.Execution;

/// <summary>
/// Issues, tracks, and automatically revokes short-lived <see cref="FileSystemSelectedResourceHandle"/>
/// grants, per `P1-EXEC-005`/`P1-EXEC-006`.
/// </summary>
/// <remarks>
/// Subscribes to <see cref="SessionLoggedOutEvent"/> (revokes every handle for that user),
/// <see cref="SessionShutDownEvent"/> (revokes every tracked handle), and
/// <see cref="ProcessStateChangedEvent"/> reaching a terminal state (revokes every handle issued
/// to that process). Expiry is evaluated lazily by <see cref="FileSystemSelectedResourceHandle.Allows"/>
/// at use time using the deterministic simulation clock, so no separate timer is required.
/// </remarks>
public sealed class FileSystemSelectedResourceHandleRegistry : IFileSystemSelectedResourceHandleRegistry, IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, FileSystemSelectedResourceHandle> _handles = [];
    private readonly Dictionary<Guid, ProcessId?> _issuedToProcess = [];
    private readonly ISimulationClock _clock;
    private readonly ICapabilityGrantRepository _grantRepository;
    private readonly List<IDisposable> _subscriptions = [];

    /// <summary>Initializes a registry wired to auto-revoke on session/process lifecycle events.</summary>
    public FileSystemSelectedResourceHandleRegistry(
        ISimulationClock clock,
        ICapabilityGrantRepository grantRepository,
        IEventBus? eventBus = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _grantRepository = grantRepository ?? throw new ArgumentNullException(nameof(grantRepository));

        if (eventBus is not null)
        {
            _subscriptions.Add(eventBus.Subscribe<SessionLoggedOutEvent>(e => RevokeAllForUser(e.UserId.ToString())));
            _subscriptions.Add(eventBus.Subscribe<SessionShutDownEvent>(_ => RevokeAll()));
            _subscriptions.Add(eventBus.Subscribe<AppDisabledEvent>(e => RevokeAllForApp(e.AppId)));
            _subscriptions.Add(eventBus.Subscribe<ProcessStateChangedEvent>(e =>
            {
                if (e.NewState is ProcessState.Stopped or ProcessState.Faulted)
                {
                    RevokeAllForProcess(e.Pid);
                }
            }));
        }
    }

    /// <inheritdoc />
    public FileSystemSelectedResourceHandle Issue(
        string appId,
        string userId,
        VirtualPath path,
        FileSystemHandleAccess access,
        TimeSpan validFor,
        ProcessId? issuedToProcessId = null)
    {
        DateTimeOffset issuedAt = _clock.UtcNow;
        FileSystemSelectedResourceHandle handle = new(
            Guid.NewGuid(),
            appId,
            userId,
            path,
            access,
            issuedAt,
            issuedAt + validFor,
            Math.Max(_grantRepository.CurrentPolicyRevision, 1));

        lock (_lock)
        {
            _handles[handle.Id] = handle;
            _issuedToProcess[handle.Id] = issuedToProcessId;
        }

        return handle;
    }

    /// <inheritdoc />
    public bool TryGet(Guid handleId, out FileSystemSelectedResourceHandle handle)
    {
        lock (_lock)
        {
            return _handles.TryGetValue(handleId, out handle!);
        }
    }

    /// <inheritdoc />
    public bool Revoke(Guid handleId)
    {
        lock (_lock)
        {
            return RevokeLocked(handleId);
        }
    }

    /// <inheritdoc />
    public int RevokeAllForProcess(ProcessId processId)
    {
        lock (_lock)
        {
            int count = 0;
            foreach ((Guid id, ProcessId? owner) in _issuedToProcess)
            {
                if (owner == processId && RevokeLocked(id))
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <inheritdoc />
    public int RevokeAllForUser(string userId)
    {
        lock (_lock)
        {
            int count = 0;
            foreach (Guid id in _handles.Keys.ToArray())
            {
                if (string.Equals(_handles[id].UserId, userId, StringComparison.Ordinal) && RevokeLocked(id))
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <inheritdoc />
    public int RevokeAllForApp(string appId)
    {
        lock (_lock)
        {
            int count = 0;
            foreach (Guid id in _handles.Keys.ToArray())
            {
                if (string.Equals(_handles[id].AppId, appId, StringComparison.Ordinal) && RevokeLocked(id))
                {
                    count++;
                }
            }

            return count;
        }
    }

    private int RevokeAll()
    {
        lock (_lock)
        {
            int count = 0;
            foreach (Guid id in _handles.Keys.ToArray())
            {
                if (RevokeLocked(id))
                {
                    count++;
                }
            }

            return count;
        }
    }

    private bool RevokeLocked(Guid handleId)
    {
        if (!_handles.TryGetValue(handleId, out FileSystemSelectedResourceHandle? handle) || handle.Revoked)
        {
            return false;
        }

        _handles[handleId] = new FileSystemSelectedResourceHandle(
            handle.Id,
            handle.AppId,
            handle.UserId,
            handle.Path,
            handle.Access,
            handle.IssuedAtUtc,
            handle.ExpiresAtUtc,
            handle.PolicyRevision,
            revoked: true);
        return true;
    }

    /// <summary>Unsubscribes from every wired lifecycle event.</summary>
    public void Dispose()
    {
        foreach (IDisposable subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }
}
