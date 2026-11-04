using System.Text.Json;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Infrastructure.Browser.Backup;
using Kazo.Os.Infrastructure.Browser.Storage;
using Kazo.Os.Simulation.Abstractions.Recovery;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Recovery;

/// <summary>Maps browser persistence failures to the renderer-independent recovery contract.</summary>
public static class BrowserStorageRecoveryClassifier
{
    /// <summary>Classifies one failure without performing recovery or mutating storage.</summary>
    public static StorageRecoveryPresentation Classify(
        Exception exception,
        StorageRecoveryContext context,
        Guid correlationId)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (correlationId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty correlation ID is required.", nameof(correlationId));
        }

        bool blocksBoot = context is StorageRecoveryContext.Boot or StorageRecoveryContext.Migration;
        return exception switch
        {
            BrowserStorageQuotaException => Presentation(
                StorageRecoveryState.QuotaExhausted,
                "storage.quota-exhausted",
                blocksBoot,
                canExport: true,
                StorageRecoveryActions.Export | StorageRecoveryActions.Cleanup | StorageRecoveryActions.Retry,
                correlationId),
            IndexedDbBackupValidationException => Presentation(
                StorageRecoveryState.BackupValidationFailed,
                "storage.backup-invalid",
                blocksBoot: false,
                canExport: true,
                StorageRecoveryActions.Export | StorageRecoveryActions.RestoreMerge,
                correlationId),
            CapabilityGrantConflictException => Presentation(
                StorageRecoveryState.RecoverableConflict,
                "storage.operation-conflict",
                blocksBoot: false,
                canExport: false,
                StorageRecoveryActions.Retry,
                correlationId),
            JSException jsException when context == StorageRecoveryContext.Migration => Presentation(
                StorageRecoveryState.MigrationFailed,
                "storage.migration-failed",
                blocksBoot: true,
                canExport: true,
                StorageRecoveryActions.DiagnoseReadOnly | StorageRecoveryActions.Export | StorageRecoveryActions.Retry,
                correlationId),
            JSException jsException when IsUnavailable(jsException) => Presentation(
                StorageRecoveryState.StorageUnavailable,
                "storage.unavailable",
                blocksBoot,
                canExport: false,
                StorageRecoveryActions.Retry | StorageRecoveryActions.DiagnoseReadOnly,
                correlationId),
            InvalidDataException or JsonException => Presentation(
                StorageRecoveryState.ContentInvalid,
                "storage.content-invalid",
                blocksBoot,
                canExport: true,
                StorageRecoveryActions.DiagnoseReadOnly | StorageRecoveryActions.Export
                    | StorageRecoveryActions.RestoreMerge | StorageRecoveryActions.RestoreReplace,
                correlationId),
            _ => Presentation(
                StorageRecoveryState.StorageUnavailable,
                "storage.failure",
                blocksBoot,
                canExport: false,
                StorageRecoveryActions.Retry | StorageRecoveryActions.DiagnoseReadOnly,
                correlationId)
        };
    }

    /// <summary>Creates the mandatory phrase confirmation for replacement or reset.</summary>
    public static StorageRecoveryPresentation AwaitDestructiveConfirmation(
        StorageRecoveryActions action,
        string affectedData,
        Guid correlationId)
    {
        if (action is not (StorageRecoveryActions.RestoreReplace or StorageRecoveryActions.Reset))
        {
            throw new ArgumentOutOfRangeException(nameof(action), "Only replace or reset is destructive.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(affectedData);
        if (correlationId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty correlation ID is required.", nameof(correlationId));
        }

        string phrase = action == StorageRecoveryActions.RestoreReplace ? "REPLACE" : "RESET";
        return new StorageRecoveryPresentation(
            StorageRecoveryState.AwaitingDestructiveConfirmation,
            action == StorageRecoveryActions.RestoreReplace
                ? "storage.confirm-replace"
                : "storage.confirm-reset",
            BlocksBoot: true,
            CanExport: true,
            Actions: StorageRecoveryActions.Export | action,
            correlationId,
            new StorageDestructiveConfirmation(action, phrase, affectedData));
    }

    private static bool IsUnavailable(JSException exception) =>
        exception.Message.Contains("NotAllowedError", StringComparison.Ordinal)
        || exception.Message.Contains("SecurityError", StringComparison.Ordinal)
        || exception.Message.Contains("InvalidStateError", StringComparison.Ordinal)
        || exception.Message.Contains("NotSupportedError", StringComparison.Ordinal);

    private static StorageRecoveryPresentation Presentation(
        StorageRecoveryState state,
        string errorCode,
        bool blocksBoot,
        bool canExport,
        StorageRecoveryActions actions,
        Guid correlationId) => new(
            state,
            errorCode,
            blocksBoot,
            canExport,
            actions,
            correlationId);
}