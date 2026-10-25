using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Events;
using HackerOs.Platform.Core.FileSystem.Virtual;
using HackerOs.Platform.Core.Metrics;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Metrics;
using HackerOs.Simulation.Abstractions.Processes;

namespace HackerOs.Platform.Core.FileSystem;

/// <summary>Coordinates traversal, authorization, mounts, and provider operations.</summary>
/// <remarks>
/// Publishes a <see cref="FileSystemChangeEvent"/> to <see cref="FileSystemTopics.ForDirectory"/> of the
/// affected entry's parent after every successful mutation, per
/// <c>doc/en/wasm/dev/adr/0038-emitter-authorized-topic-messaging.md</c> and
/// <c>doc/en/wasm/dev/old/Global-FileView-And-MessagingSystem/MessagingSystem.md</c> (<c>MSG-011</c>/<c>MSG-012</c>).
/// The <c>shared/filesystem/changed</c> channel is registered once, in this singleton's constructor,
/// owner-only for publish under <see cref="KernelPublisherIdentity"/> so no app can ever forge a change
/// notification. Paths owned by an <see cref="IVirtualFileSystemProvider"/> (<c>/proc</c>, <c>/dev</c>)
/// never publish one (ADR 0046).
/// <para>
/// It is also where the bytes each process reads and writes are counted for
/// <c>/proc/&lt;pid&gt;/io</c> (ADR 0057 section 3): every app gateway and every command reaches
/// files through this service, and only here is the mount known, which tells persistent storage
/// (<c>read_bytes</c>, <c>write_bytes</c>) from the virtual <c>/proc</c> and <c>/dev</c>.
/// </para>
/// <para>
/// With an <see cref="IStorageSpaceGuard"/>, it also keeps the root reserve of ADR 0099 (section 2):
/// once the free space is inside it, a create, write or copy into persistent storage by a
/// non-administrator fails with <see cref="FileSystemErrorCode.NoSpace"/> before reaching the
/// provider, while the system (logs) and the administrator keep writing.
/// </para>
/// </remarks>
public sealed class FileSystemService : IFileSystemService
{
    private readonly IFileSystemMountRouter _router;
    private readonly IFileSystemPathResolver _pathResolver;
    private readonly IFileSystemAuthorizer _authorizer;
    private readonly ITopicMessageBus _topicBus;
    private readonly Func<Guid> _transactionIdFactory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IProcessIoAccounting? _ioAccounting;
    private readonly IFileSystemAuditSink? _audit;
    private readonly IStorageSpaceGuard? _spaceGuard;

    /// <summary>Initializes the mounted filesystem service.</summary>
    /// <param name="router">Mount table.</param>
    /// <param name="pathResolver">Link-aware path resolver.</param>
    /// <param name="authorizer">Permission and capability policy.</param>
    /// <param name="topicBus">Bus the change notifications are published on.</param>
    /// <param name="transactionIdFactory">Optional deterministic transaction ID source for tests.</param>
    /// <param name="clock">Optional time source for tests.</param>
    /// <param name="ioAccounting">
    /// Optional per-process I/O accounting (ADR 0057); when present, the bytes a process reads and
    /// writes through this service are charged to the PID of its operation context.
    /// </param>
    /// <param name="audit">
    /// Optional audit sink (ADR 0100 section 3): every create, write, delete, rename, copy and
    /// <c>chmod</c> under <c>/etc</c>, <c>/bin</c> and <c>/var</c> (except <c>/var/log</c>), allowed or refused.
    /// </param>
    /// <param name="spaceGuard">
    /// Optional free-space state (ADR 0099 section 2): enforces the root reserve and is told to
    /// measure again after persistent writes.
    /// </param>
    public FileSystemService(
        IFileSystemMountRouter router,
        IFileSystemPathResolver pathResolver,
        IFileSystemAuthorizer authorizer,
        ITopicMessageBus topicBus,
        Func<Guid>? transactionIdFactory = null,
        Func<DateTimeOffset>? clock = null,
        IProcessIoAccounting? ioAccounting = null,
        IFileSystemAuditSink? audit = null,
        IStorageSpaceGuard? spaceGuard = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _topicBus = topicBus ?? throw new ArgumentNullException(nameof(topicBus));
        _transactionIdFactory = transactionIdFactory ?? Guid.NewGuid;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        _ioAccounting = ioAccounting;
        _audit = audit;
        _spaceGuard = spaceGuard;

        _topicBus.RegisterSharedChannel(
            TopicNames.Shared("filesystem").Segment("changed").Build(),
            new SharedChannelPolicy(SharedChannelAccessMode.OwnerOnly, SharedChannelAccessMode.Open),
            KernelPublisherIdentity.Value);
    }

    /// <inheritdoc />
    public string ProviderId => "mounted-filesystem";

    /// <inheritdoc />
    public async ValueTask<FileSystemResult<FileSystemContentReadHandle>> ReadAsync(
        FileSystemReadRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        FileSystemResult<FileSystemPathResolution> resolution = await ResolveAsync(
            request.Path,
            request.LinkBehavior,
            FileSystemOperation.Read,
            context,
            cancellationToken);
        if (!resolution.Succeeded)
        {
            return FileSystemResult<FileSystemContentReadHandle>.Failure(resolution.Error!);
        }

        VirtualPath path = resolution.Value!.Path;
        FileSystemError? denial = await AuthorizeAsync(
            path,
            FileSystemOperation.Read,
            FileSystemAccess.Read,
            FileSystemHandleAccess.Read,
            isWrite: false,
            context,
            cancellationToken);
        if (denial is not null)
        {
            return FileSystemResult<FileSystemContentReadHandle>.Failure(denial);
        }

        FileSystemMountResolution route = _router.Resolve(path);
        FileSystemResult<FileSystemContentReadHandle> read = await route.Mount.Provider.ReadAsync(
            new FileSystemReadRequest(path, FileSystemLinkBehavior.NoFollow),
            context,
            cancellationToken);
        return read.Succeeded && AccountedProcess(context) is { } pid
            ? FileSystemResult<FileSystemContentReadHandle>.Success(
                CountReads(read.Value!, pid, fromStorage: route.Mount.Provider is not IVirtualFileSystemProvider))
            : read;
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemResult<FileSystemDirectorySnapshot>> EnumerateAsync(
        FileSystemEnumerateRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        FileSystemResult<FileSystemPathResolution> resolution = await ResolveAsync(
            request.Path,
            request.LinkBehavior,
            FileSystemOperation.Enumerate,
            context,
            cancellationToken);
        if (!resolution.Succeeded)
        {
            return FileSystemResult<FileSystemDirectorySnapshot>.Failure(resolution.Error!);
        }

        VirtualPath path = resolution.Value!.Path;
        FileSystemError? denial = await AuthorizeAsync(
            path,
            FileSystemOperation.Enumerate,
            FileSystemAccess.Read | FileSystemAccess.Execute,
            FileSystemHandleAccess.Enumerate,
            isWrite: false,
            context,
            cancellationToken);
        if (denial is not null)
        {
            return FileSystemResult<FileSystemDirectorySnapshot>.Failure(denial);
        }

        FileSystemMountResolution route = _router.Resolve(path);
        return await route.Mount.Provider.EnumerateAsync(
            new FileSystemEnumerateRequest(path, FileSystemLinkBehavior.NoFollow),
            context,
            cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemMutationResult> CreateAsync(
        FileSystemCreateRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        FileSystemMutationResult result = await CreateCoreAsync(request, context, cancellationToken).ConfigureAwait(false);
        if (_audit is not null && FileSystemAuditPolicy.IsAudited(request.Path))
        {
            _audit.Record("create", request.Path, null, context.OperationContext, result);
        }

        return result;
    }

    private async ValueTask<FileSystemMutationResult> CreateCoreAsync(
        FileSystemCreateRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken)
    {
        VirtualPath requestedParent = GetParent(request.Path);
        FileSystemResult<FileSystemPathResolution> parentResolution = await ResolveAsync(
            requestedParent,
            FileSystemLinkBehavior.Follow,
            FileSystemOperation.Create,
            context,
            cancellationToken);
        if (!parentResolution.Succeeded)
        {
            return Reject(parentResolution.Error!);
        }

        VirtualPath parent = parentResolution.Value!.Path;
        VirtualPath path = Combine(parent, GetName(request.Path));
        FileSystemError? denial = await AuthorizeAsync(
            parent,
            FileSystemOperation.Create,
            FileSystemAccess.Write | FileSystemAccess.Execute,
            FileSystemHandleAccess.Write,
            isWrite: true,
            context,
            cancellationToken,
            authorizationPath: path);
        if (denial is not null)
        {
            return Reject(denial);
        }

        FileSystemMountResolution route = _router.Resolve(path);
        if (CheckReservedSpace(FileSystemOperation.Create, path, route.Mount.Provider, context) is { } full)
        {
            return Reject(full);
        }

        FileSystemMutationResult result = await route.Mount.Provider.CreateAsync(
            new FileSystemCreateRequest(
                path,
                request.Kind,
                request.Permissions,
                request.ExpectedParentRevision,
                request.SymbolicLinkTarget),
            context,
            cancellationToken);
        NoteSpaceUse(result, route.Mount.Provider);
        if (result.Transaction.Status == FileSystemTransactionStatus.Committed && result.Entry is not null)
        {
            PublishChange(path, result.Entry.Metadata.Kind, result.Entry.Metadata.Revision, FileSystemChangeKind.Created);
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemMutationResult> WriteAsync(
        FileSystemWriteRequest request,
        IFileSystemContentSource content,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        FileSystemMutationResult result = await WriteCoreAsync(request, content, context, cancellationToken).ConfigureAwait(false);
        if (_audit is not null && FileSystemAuditPolicy.IsAudited(request.Path))
        {
            _audit.Record("write", request.Path, null, context.OperationContext, result);
        }

        return result;
    }

    private async ValueTask<FileSystemMutationResult> WriteCoreAsync(
        FileSystemWriteRequest request,
        IFileSystemContentSource content,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemPathResolution> resolution = await ResolveAsync(
            request.Path,
            FileSystemLinkBehavior.Follow,
            FileSystemOperation.Write,
            context,
            cancellationToken);
        if (!resolution.Succeeded)
        {
            return Reject(resolution.Error!);
        }

        VirtualPath path = resolution.Value!.Path;
        FileSystemError? denial = await AuthorizeAsync(
            path,
            FileSystemOperation.Write,
            FileSystemAccess.Write,
            FileSystemHandleAccess.Write,
            isWrite: true,
            context,
            cancellationToken);
        if (denial is not null)
        {
            return Reject(denial);
        }

        IFileSystemProvider target = _router.Resolve(path).Mount.Provider;
        if (CheckReservedSpace(FileSystemOperation.Write, path, target, context) is { } full)
        {
            return Reject(full);
        }

        ProcessId? writer = AccountedProcess(context);
        WriteCountingContentSource? counted = writer is null ? null : new WriteCountingContentSource(content);
        FileSystemMutationResult result = await target.WriteAsync(
            new FileSystemWriteRequest(path, request.ExpectedRevision),
            (IFileSystemContentSource?)counted ?? content,
            context,
            cancellationToken);
        if (counted is { Opened: true })
        {
            // One write(2) per call: the provider consumes the whole content in one operation.
            _ioAccounting!.RecordWrite(writer!.Value, counted.Bytes, toStorage: target is not IVirtualFileSystemProvider);
        }

        NoteSpaceUse(result, target);

        if (result.Transaction.Status == FileSystemTransactionStatus.Committed && result.Entry is not null)
        {
            PublishChange(path, result.Entry.Metadata.Kind, result.Entry.Metadata.Revision, FileSystemChangeKind.ContentModified);
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemMutationResult> MoveAsync(
        FileSystemMoveRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        FileSystemMutationResult result = await MoveCoreAsync(request, context, cancellationToken).ConfigureAwait(false);
        if (_audit is not null && (FileSystemAuditPolicy.IsAudited(request.SourcePath) || FileSystemAuditPolicy.IsAudited(request.DestinationPath)))
        {
            _audit.Record("rename", request.SourcePath, request.DestinationPath, context.OperationContext, result);
        }

        return result;
    }

    private async ValueTask<FileSystemMutationResult> MoveCoreAsync(
        FileSystemMoveRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemPathResolution> sourceResolution = await ResolveAsync(
            request.SourcePath,
            FileSystemLinkBehavior.NoFollow,
            FileSystemOperation.Move,
            context,
            cancellationToken);
        FileSystemResult<FileSystemPathResolution> destinationParentResolution = await ResolveAsync(
            GetParent(request.DestinationPath),
            FileSystemLinkBehavior.Follow,
            FileSystemOperation.Move,
            context,
            cancellationToken);
        if (!sourceResolution.Succeeded || !destinationParentResolution.Succeeded)
        {
            return Reject(sourceResolution.Error ?? destinationParentResolution.Error!);
        }

        VirtualPath source = sourceResolution.Value!.Path;
        VirtualPath sourceParent = GetParent(source);
        VirtualPath destinationParent = destinationParentResolution.Value!.Path;
        VirtualPath destination = Combine(destinationParent, GetName(request.DestinationPath));
        FileSystemMountResolution sourceRoute = _router.Resolve(source);
        FileSystemMountResolution destinationRoute = _router.Resolve(destination);
        if (!ReferenceEquals(sourceRoute.Mount.Provider, destinationRoute.Mount.Provider))
        {
            return Reject(new FileSystemError(
                FileSystemOperation.Move,
                FileSystemErrorCode.CrossMountOperation,
                source,
                destination));
        }

        FileSystemError? sourceDenial = await AuthorizeAsync(
            sourceParent,
            FileSystemOperation.Move,
            FileSystemAccess.Write | FileSystemAccess.Execute,
            FileSystemHandleAccess.Write,
            isWrite: true,
            context,
            cancellationToken,
            authorizationPath: source);
        FileSystemError? destinationDenial = await AuthorizeAsync(
            destinationParent,
            FileSystemOperation.Move,
            FileSystemAccess.Write | FileSystemAccess.Execute,
            FileSystemHandleAccess.Write,
            isWrite: true,
            context,
            cancellationToken,
            authorizationPath: destination);
        if (sourceDenial is not null || destinationDenial is not null)
        {
            return Reject(sourceDenial ?? destinationDenial!);
        }

        FileSystemMutationResult result = await sourceRoute.Mount.Provider.MoveAsync(
            new FileSystemMoveRequest(
                source,
                destination,
                request.ExpectedEntryRevision,
                request.ExpectedSourceParentRevision,
                request.ExpectedDestinationParentRevision),
            context,
            cancellationToken);
        if (result.Transaction.Status == FileSystemTransactionStatus.Committed && result.Entry is not null)
        {
            PublishChange(
                source, result.Entry.Metadata.Kind, result.Entry.Metadata.Revision, FileSystemChangeKind.MovedFrom, destination);
            if (destinationParent != sourceParent)
            {
                PublishChange(destination, result.Entry.Metadata.Kind, result.Entry.Metadata.Revision, FileSystemChangeKind.MovedTo);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemMutationResult> CopyAsync(
        FileSystemCopyRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        FileSystemMutationResult result = await CopyCoreAsync(request, context, cancellationToken).ConfigureAwait(false);
        if (_audit is not null && FileSystemAuditPolicy.IsAudited(request.DestinationPath))
        {
            _audit.Record("copy", request.DestinationPath, null, context.OperationContext, result);
        }

        return result;
    }

    private async ValueTask<FileSystemMutationResult> CopyCoreAsync(
        FileSystemCopyRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemPathResolution> sourceResolution = await ResolveAsync(
            request.SourcePath,
            FileSystemLinkBehavior.NoFollow,
            FileSystemOperation.Copy,
            context,
            cancellationToken);
        FileSystemResult<FileSystemPathResolution> destinationParentResolution = await ResolveAsync(
            GetParent(request.DestinationPath),
            FileSystemLinkBehavior.Follow,
            FileSystemOperation.Copy,
            context,
            cancellationToken);
        if (!sourceResolution.Succeeded || !destinationParentResolution.Succeeded)
        {
            return Reject(sourceResolution.Error ?? destinationParentResolution.Error!);
        }

        VirtualPath source = sourceResolution.Value!.Path;
        VirtualPath destinationParent = destinationParentResolution.Value!.Path;
        VirtualPath destination = Combine(destinationParent, GetName(request.DestinationPath));
        FileSystemMountResolution sourceRoute = _router.Resolve(source);
        FileSystemMountResolution destinationRoute = _router.Resolve(destination);
        if (!ReferenceEquals(sourceRoute.Mount.Provider, destinationRoute.Mount.Provider))
        {
            return Reject(new FileSystemError(
                FileSystemOperation.Copy,
                FileSystemErrorCode.CrossMountOperation,
                source,
                destination));
        }

        FileSystemError? sourceDenial = await AuthorizeAsync(
            source,
            FileSystemOperation.Copy,
            FileSystemAccess.Read,
            FileSystemHandleAccess.Read,
            isWrite: false,
            context,
            cancellationToken);
        FileSystemError? destinationDenial = await AuthorizeAsync(
            destinationParent,
            FileSystemOperation.Copy,
            FileSystemAccess.Write | FileSystemAccess.Execute,
            FileSystemHandleAccess.Write,
            isWrite: true,
            context,
            cancellationToken,
            authorizationPath: destination);
        if (sourceDenial is not null || destinationDenial is not null)
        {
            return Reject(sourceDenial ?? destinationDenial!);
        }

        if (CheckReservedSpace(FileSystemOperation.Copy, destination, destinationRoute.Mount.Provider, context) is { } full)
        {
            return Reject(full);
        }

        FileSystemMutationResult result = await sourceRoute.Mount.Provider.CopyAsync(
            new FileSystemCopyRequest(
                source,
                destination,
                request.ExpectedEntryRevision,
                request.ExpectedDestinationParentRevision),
            context,
            cancellationToken);
        NoteSpaceUse(result, sourceRoute.Mount.Provider);
        if (result.Transaction.Status == FileSystemTransactionStatus.Committed && result.Entry is not null)
        {
            PublishChange(destination, result.Entry.Metadata.Kind, result.Entry.Metadata.Revision, FileSystemChangeKind.Created);
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemMutationResult> DeleteAsync(
        FileSystemDeleteRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        FileSystemMutationResult result = await DeleteCoreAsync(request, context, cancellationToken).ConfigureAwait(false);
        if (_audit is not null && FileSystemAuditPolicy.IsAudited(request.Path))
        {
            _audit.Record("delete", request.Path, null, context.OperationContext, result);
        }

        return result;
    }

    private async ValueTask<FileSystemMutationResult> DeleteCoreAsync(
        FileSystemDeleteRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemPathResolution> resolution = await ResolveAsync(
            request.Path,
            FileSystemLinkBehavior.NoFollow,
            FileSystemOperation.Delete,
            context,
            cancellationToken);
        if (!resolution.Succeeded)
        {
            return Reject(resolution.Error!);
        }

        VirtualPath path = resolution.Value!.Path;
        FileSystemMountResolution route = _router.Resolve(path);
        if (path == route.Mount.Path && route.Mount.IsProtectedRoot)
        {
            return Reject(new FileSystemError(
                FileSystemOperation.Delete,
                FileSystemErrorCode.ProtectedEntry,
                path));
        }

        FileSystemError? denial = await AuthorizeAsync(
            GetParent(path),
            FileSystemOperation.Delete,
            FileSystemAccess.Write | FileSystemAccess.Execute,
            FileSystemHandleAccess.Write,
            isWrite: true,
            context,
            cancellationToken,
            authorizationPath: path);
        if (denial is not null)
        {
            return Reject(denial);
        }

        FileSystemResult<FileSystemEntrySnapshot> preDeleteStat = await route.Mount.Provider.StatAsync(
            new FileSystemStatRequest(path, FileSystemLinkBehavior.NoFollow), context, cancellationToken);

        FileSystemMutationResult result = await route.Mount.Provider.DeleteAsync(
            new FileSystemDeleteRequest(
                path,
                request.ExpectedEntryRevision,
                request.ExpectedParentRevision,
                request.Recursive),
            context,
            cancellationToken);
        if (result.Transaction.Status == FileSystemTransactionStatus.Committed && preDeleteStat.Succeeded)
        {
            VirtualPath parent = GetParent(path);
            FileSystemResult<FileSystemEntrySnapshot> parentStat = await route.Mount.Provider.StatAsync(
                new FileSystemStatRequest(parent, FileSystemLinkBehavior.NoFollow), context, cancellationToken);
            long revision = parentStat.Succeeded ? parentStat.Value!.Metadata.Revision : preDeleteStat.Value!.Metadata.Revision;
            PublishChange(path, preDeleteStat.Value!.Metadata.Kind, revision, FileSystemChangeKind.Deleted);
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemResult<FileSystemEntrySnapshot>> StatAsync(
        FileSystemStatRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        FileSystemResult<FileSystemPathResolution> resolution = await ResolveAsync(
            request.Path,
            request.LinkBehavior,
            FileSystemOperation.Stat,
            context,
            cancellationToken);
        if (!resolution.Succeeded)
        {
            return FileSystemResult<FileSystemEntrySnapshot>.Failure(resolution.Error!);
        }

        VirtualPath path = resolution.Value!.Path;
        FileSystemError? denial = await AuthorizeAsync(
            path,
            FileSystemOperation.Stat,
            FileSystemAccess.Read,
            FileSystemHandleAccess.Metadata,
            isWrite: false,
            context,
            cancellationToken);
        if (denial is not null)
        {
            return FileSystemResult<FileSystemEntrySnapshot>.Failure(denial);
        }

        return await _router.Resolve(path).Mount.Provider.StatAsync(
            new FileSystemStatRequest(path, FileSystemLinkBehavior.NoFollow),
            context,
            cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemMutationResult> SetPermissionsAsync(
        FileSystemSetPermissionsRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        FileSystemMutationResult result = await SetPermissionsCoreAsync(request, context, cancellationToken).ConfigureAwait(false);
        if (_audit is not null && FileSystemAuditPolicy.IsAudited(request.Path))
        {
            _audit.Record("chmod", request.Path, null, context.OperationContext, result);
        }

        return result;
    }

    private async ValueTask<FileSystemMutationResult> SetPermissionsCoreAsync(
        FileSystemSetPermissionsRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemPathResolution> resolution = await ResolveAsync(
            request.Path,
            request.LinkBehavior,
            FileSystemOperation.SetPermissions,
            context,
            cancellationToken);
        if (!resolution.Succeeded)
        {
            return Reject(resolution.Error!);
        }

        VirtualPath path = resolution.Value!.Path;
        FileSystemError? denial = await AuthorizeAsync(
            path,
            FileSystemOperation.SetPermissions,
            FileSystemAccess.Write,
            FileSystemHandleAccess.Write,
            isWrite: true,
            context,
            cancellationToken);
        if (denial is not null)
        {
            return Reject(denial);
        }

        FileSystemMutationResult result = await _router.Resolve(path).Mount.Provider.SetPermissionsAsync(
            new FileSystemSetPermissionsRequest(
                path,
                request.Permissions,
                request.ExpectedRevision,
                FileSystemLinkBehavior.NoFollow),
            context,
            cancellationToken);
        if (result.Transaction.Status == FileSystemTransactionStatus.Committed && result.Entry is not null)
        {
            PublishChange(path, result.Entry.Metadata.Kind, result.Entry.Metadata.Revision, FileSystemChangeKind.MetadataModified);
        }

        return result;
    }

    private ValueTask<FileSystemResult<FileSystemPathResolution>> ResolveAsync(
        VirtualPath path,
        FileSystemLinkBehavior linkBehavior,
        FileSystemOperation operation,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken) =>
        _pathResolver.ResolveAsync(path, linkBehavior, operation, context, cancellationToken);

    private async ValueTask<FileSystemError?> AuthorizeAsync(
        VirtualPath metadataPath,
        FileSystemOperation operation,
        FileSystemAccess requiredAccess,
        FileSystemHandleAccess handleAccess,
        bool isWrite,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken,
        VirtualPath? authorizationPath = null)
    {
        FileSystemResult<FileSystemEntrySnapshot> stat = await _router.Resolve(metadataPath).Mount.Provider.StatAsync(
            new FileSystemStatRequest(metadataPath, FileSystemLinkBehavior.NoFollow),
            context,
            cancellationToken);
        if (!stat.Succeeded)
        {
            return new FileSystemError(
                operation,
                stat.Error?.Code ?? FileSystemErrorCode.ProviderFailure,
                authorizationPath ?? metadataPath);
        }

        VirtualPath policyPath = authorizationPath ?? metadataPath;
        FileSystemAuthorizationResult authorization = _authorizer.Authorize(
            new FileSystemAuthorizationRequest(
                operation,
                policyPath,
                stat.Value!.Metadata,
                requiredAccess,
                ResolveCapability(policyPath, context, isWrite),
                handleAccess,
                isWrite && IsSystemPath(policyPath)
                    ? AppAuthority.Administrator
                    : AppAuthority.User,
                context));
        return authorization.Error;
    }

    private static string ResolveCapability(
        VirtualPath path,
        FileSystemAuthorizationContext context,
        bool isWrite)
    {
        string userId = context.OperationContext.UserId;
        string? username = context.OperationContext.UserName;
        string appId = context.OperationContext.AppId;

        // ADR 0044: an app's own user settings layer, ~/.config/<appId>/, is app-private storage.
        if (!string.IsNullOrWhiteSpace(username) && IsWithin(path, $"/home/{username}/.config/{appId}"))
        {
            return isWrite ? AppCapabilities.FileSystemPrivateWrite : AppCapabilities.FileSystemPrivateRead;
        }

        if (IsWithin(path, $"/home/{userId}/.config/{appId}"))
        {
            return isWrite ? AppCapabilities.FileSystemPrivateWrite : AppCapabilities.FileSystemPrivateRead;
        }

        // ADR 0044: an app reads its own system settings layer, /etc/<appId>/, without the broad
        // filesystem.system.read; writing it stays filesystem.system.write plus Administrator
        // authority and mode bits (the files belong to system:system).
        if (!isWrite && IsWithin(path, $"/etc/{appId}"))
        {
            return AppCapabilities.FileSystemPrivateRead;
        }

        if (!string.IsNullOrWhiteSpace(username) && IsWithin(path, $"/home/{username}"))
        {
            return isWrite ? AppCapabilities.FileSystemUserHomeWrite : AppCapabilities.FileSystemUserHomeRead;
        }

        if (IsWithin(path, $"/home/{userId}"))
        {
            return isWrite ? AppCapabilities.FileSystemUserHomeWrite : AppCapabilities.FileSystemUserHomeRead;
        }

        return isWrite ? AppCapabilities.FileSystemSystemWrite : AppCapabilities.FileSystemSystemRead;
    }

    private static bool IsSystemPath(VirtualPath path) =>
        IsWithin(path, "/etc") || IsWithin(path, "/bin") || IsWithin(path, "/var");

    private static bool IsWithin(VirtualPath path, string root) =>
        path.Value == root || path.Value.StartsWith($"{root}/", StringComparison.Ordinal);

    /// <summary>
    /// The root reserve (ADR 0099 section 2): inside it, a persistent create, write or copy by
    /// anyone below administrator authority is refused with <c>ENOSPC</c>. Virtual mounts take no
    /// space and are never refused.
    /// </summary>
    private FileSystemError? CheckReservedSpace(
        FileSystemOperation operation,
        VirtualPath path,
        IFileSystemProvider target,
        FileSystemAuthorizationContext context)
    {
        if (_spaceGuard is not { IsRootReserveReached: true }
            || target is IVirtualFileSystemProvider
            || AppAuthorityPolicy.Satisfies(context.OperationContext.EffectiveAuthority, AppAuthority.Administrator))
        {
            return null;
        }

        // Space may have been freed since the last measure: look again soon.
        _spaceGuard.RequestRefresh();
        return new FileSystemError(operation, FileSystemErrorCode.NoSpace, path);
    }

    /// <summary>A persistent mutation changed the used space, or hit the real limit: ask for a new measure.</summary>
    private void NoteSpaceUse(FileSystemMutationResult result, IFileSystemProvider target)
    {
        if (_spaceGuard is not null && target is not IVirtualFileSystemProvider
            && (result.Succeeded || result.Transaction.Error?.Code == FileSystemErrorCode.NoSpace))
        {
            _spaceGuard.RequestRefresh();
        }
    }

    private FileSystemMutationResult Reject(FileSystemError error)
    {
        Guid transactionId = _transactionIdFactory();
        if (transactionId == Guid.Empty)
        {
            throw new InvalidOperationException("The transaction ID factory returned an empty ID.");
        }

        FileSystemTransactionResult transaction = error.Code == FileSystemErrorCode.Cancelled
            ? FileSystemTransactionResult.Cancelled(transactionId, error)
            : FileSystemTransactionResult.Rejected(transactionId, error);
        return new FileSystemMutationResult(transaction);
    }

    private static VirtualPath GetParent(VirtualPath path)
    {
        int separator = path.Value.LastIndexOf('/');
        return separator <= 0 ? VirtualPath.Parse("/") : VirtualPath.Parse(path.Value[..separator]);
    }

    private static FileSystemEntryName GetName(VirtualPath path) =>
        FileSystemEntryName.Parse(path.Value[(path.Value.LastIndexOf('/') + 1)..]);

    private static VirtualPath Combine(VirtualPath parent, FileSystemEntryName name) =>
        VirtualPath.Parse(parent.Value == "/" ? $"/{name.Value}" : $"{parent.Value}/{name.Value}");

    /// <summary>
    /// Gets the process an operation is charged to: its PID when I/O accounting is on and the
    /// caller is a process (system operations outside any process are not charged).
    /// </summary>
    private ProcessId? AccountedProcess(FileSystemAuthorizationContext context) =>
        _ioAccounting is not null && context.OperationContext.ProcessId is > 0 and { } pid
            ? ProcessId.FromInt64(pid)
            : null;

    /// <summary>Wraps a read handle so each read call on its content is charged to <paramref name="pid"/>.</summary>
    private FileSystemContentReadHandle CountReads(FileSystemContentReadHandle handle, ProcessId pid, bool fromStorage)
    {
        IProcessIoAccounting accounting = _ioAccounting!;
        return new FileSystemContentReadHandle(
            handle.Entry,
            handle.Descriptor,
            new ReadCountingStream(handle.Content, read => accounting.RecordRead(pid, read, fromStorage)));
    }

    /// <summary>Publishes one change notification for <paramref name="affectedPath"/> to its parent directory's topic.</summary>
    private void PublishChange(
        VirtualPath affectedPath,
        FileSystemEntryKind entryKind,
        long revision,
        FileSystemChangeKind kind,
        VirtualPath? movedToPath = null)
    {
        // Virtual mounts (/proc, /dev) are computed on read and never change "on disk": like
        // inotify on Linux's /proc, they never raise change notifications (ADR 0046).
        if (_router.Resolve(affectedPath).Mount.Provider is IVirtualFileSystemProvider)
        {
            return;
        }

        TopicName topic = FileSystemTopics.ForDirectory(GetParent(affectedPath));
        _topicBus.Publish(
            topic,
            new FileSystemChangeEvent(affectedPath, kind, entryKind, revision, _clock(), movedToPath),
            KernelPublisherIdentity.Value);
    }
}