using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Time;

namespace HackerOs.Platform.Core.Execution;

/// <summary>
/// Provides one app instance's authorized directory-change watch access, reusing the same
/// filesystem-read authorization <see cref="IFileSystemProvider.StatAsync"/> already applies — watching a
/// directory never reveals more than reading it already would, and no new capability identifier is
/// introduced. See <c>doc/wasm/dev/old/Global-FileView-And-MessagingSystem/MessagingSystem.md</c> (<c>MSG-013</c>).
/// </summary>
public sealed class AppFileSystemWatchGateway : IAppFileSystemWatchGateway
{
    private readonly IFileSystemService _fileSystem;
    private readonly ITopicMessageBus _topicBus;
    private readonly ISimulationClock _clock;
    private readonly AppOperationContext _operationContext;
    private readonly IReadOnlyList<string> _groupIds;
    private readonly PublisherIdentity _identity;

    /// <summary>Initializes a watch gateway bound to one app operation context.</summary>
    public AppFileSystemWatchGateway(
        IFileSystemService fileSystem,
        ITopicMessageBus topicBus,
        ISimulationClock clock,
        AppOperationContext operationContext,
        IReadOnlyList<string> groupIds,
        string processId)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _topicBus = topicBus ?? throw new ArgumentNullException(nameof(topicBus));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _operationContext = operationContext ?? throw new ArgumentNullException(nameof(operationContext));
        _groupIds = groupIds ?? throw new ArgumentNullException(nameof(groupIds));
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        _identity = new PublisherIdentity(operationContext.AppId, operationContext.UserId, processId);
    }

    /// <inheritdoc />
    public async ValueTask<ITopicChannelSubscription<FileSystemChangeEvent>> WatchAsync(
        VirtualPath path, FileSystemWatchScope scope, CancellationToken cancellationToken = default)
    {
        if (scope is FileSystemWatchScope.ThisEntry or FileSystemWatchScope.Recursive)
        {
            throw new NotSupportedException(
                $"FileSystemWatchScope.{scope} is not implemented yet — only ImmediateChildren is supported " +
                "in this pass (MSG-013); see MessagingSystem.md.");
        }

        FileSystemAuthorizationContext context = new(_operationContext, _groupIds, _clock.UtcNow, null);
        FileSystemResult<FileSystemEntrySnapshot> stat = await _fileSystem.StatAsync(
            new FileSystemStatRequest(path), context, cancellationToken);
        if (!stat.Succeeded)
        {
            // No capability grant participates in this specific denial (the real check already happened
            // inside StatAsync's own authorization), so a synthetic revision of 1 stands in for the "no
            // grant to cite" case, mirroring InMemoryTopicMessageBus's owner-only subscribe denial.
            throw new AppGatewayAccessDeniedException(
                $"filesystem-watch:{path.Value}",
                CapabilityPolicyEvaluation.DenyMissing(1));
        }

        return _topicBus.SubscribeChannel<FileSystemChangeEvent>(FileSystemTopics.ForDirectory(path), _identity);
    }
}
