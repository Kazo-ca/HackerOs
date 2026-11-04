using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.FileSystem;

/// <summary>Evaluates trusted filesystem authority, capability, handle, and mode inputs.</summary>
public sealed class FileSystemAuthorizer : IFileSystemAuthorizer
{
    /// <inheritdoc />
    public FileSystemAuthorizationResult Authorize(FileSystemAuthorizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        AppOperationContext operationContext = request.Context.OperationContext;

        if (operationContext.OneShotElevation.TryConsume(request.RequiredCapability, request.Path))
        {
            return FileSystemAuthorizationResult.Permit();
        }

        if (!AppAuthorityPolicy.Satisfies(operationContext.EffectiveAuthority, request.MinimumAuthority))
        {
            return Deny(request, FileSystemErrorCode.AuthorityDenied);
        }

        bool hasCapability = operationContext.HasCapability(request.RequiredCapability);
        bool hasSelectedAccess = request.Context.SelectedHandle?.Allows(
            operationContext.AppId,
            operationContext.UserId,
            request.Path,
            request.RequiredHandleAccess,
            request.Context.EvaluatedAtUtc) == true;

        if (!hasCapability && !hasSelectedAccess)
        {
            return Deny(request, FileSystemErrorCode.CapabilityDenied);
        }

        FileSystemAccess availableAccess = ResolveModeAccess(request.Metadata, request.Context);
        if (operationContext.EffectiveAuthority != AppAuthority.System
            && (availableAccess & request.RequiredModeAccess) != request.RequiredModeAccess)
        {
            return Deny(request, FileSystemErrorCode.PermissionDenied);
        }

        return FileSystemAuthorizationResult.Permit();
    }

    private static FileSystemAccess ResolveModeAccess(
        FileSystemEntryMetadata metadata,
        FileSystemAuthorizationContext context)
    {
        if (string.Equals(metadata.OwnerId, context.OperationContext.UserId, StringComparison.Ordinal)
            || (!string.IsNullOrWhiteSpace(context.OperationContext.UserName)
                && string.Equals(metadata.OwnerId, context.OperationContext.UserName, StringComparison.Ordinal)))
        {
            return metadata.Permissions.Owner;
        }

        return context.GroupIds.Contains(metadata.GroupId)
            ? metadata.Permissions.Group
            : metadata.Permissions.Other;
    }

    private static FileSystemAuthorizationResult Deny(
        FileSystemAuthorizationRequest request,
        FileSystemErrorCode code) =>
        FileSystemAuthorizationResult.Deny(new FileSystemError(
            request.Operation, code, request.Path, requiredCapability: request.RequiredCapability));
}