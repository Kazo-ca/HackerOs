using HackerOs.Infrastructure.Browser.Backup;
using HackerOs.Infrastructure.Browser.Recovery;
using HackerOs.Infrastructure.Browser.Storage;
using HackerOs.Simulation.Abstractions.Recovery;
using Microsoft.JSInterop;

namespace HackerOs.Infrastructure.Browser.Tests;

/// <summary>Verifies recovery states, safe actions, boot blocking, and targeted confirmation.</summary>
public sealed class BrowserStorageRecoveryClassifierTests
{
    private static readonly Guid CorrelationId =
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public void Quota_exhaustion_after_boot_preserves_export_and_does_not_block_boot()
    {
        StorageRecoveryPresentation presentation = BrowserStorageRecoveryClassifier.Classify(
            new BrowserStorageQuotaException("quota", new JSException("QuotaExceededError")),
            StorageRecoveryContext.Operation,
            CorrelationId);

        Assert.Equal(StorageRecoveryState.QuotaExhausted, presentation.State);
        Assert.Equal("storage.quota-exhausted", presentation.ErrorCode);
        Assert.False(presentation.BlocksBoot);
        Assert.True(presentation.CanExport);
        Assert.True(presentation.Actions.HasFlag(StorageRecoveryActions.Cleanup));
        Assert.True(presentation.Actions.HasFlag(StorageRecoveryActions.Export));
        Assert.False(presentation.Actions.HasFlag(StorageRecoveryActions.Reset));
    }

    [Fact]
    public void Migration_failure_blocks_boot_but_keeps_read_only_export_available()
    {
        StorageRecoveryPresentation presentation = BrowserStorageRecoveryClassifier.Classify(
            new JSException("AbortError: migration transaction aborted"),
            StorageRecoveryContext.Migration,
            CorrelationId);

        Assert.Equal(StorageRecoveryState.MigrationFailed, presentation.State);
        Assert.True(presentation.BlocksBoot);
        Assert.True(presentation.CanExport);
        Assert.True(presentation.Actions.HasFlag(StorageRecoveryActions.DiagnoseReadOnly));
        Assert.False(presentation.Actions.HasFlag(StorageRecoveryActions.RestoreReplace));
    }

    [Fact]
    public void Unavailable_storage_blocks_boot_without_claiming_export_is_possible()
    {
        StorageRecoveryPresentation presentation = BrowserStorageRecoveryClassifier.Classify(
            new JSException("SecurityError: access denied"),
            StorageRecoveryContext.Boot,
            CorrelationId);

        Assert.Equal(StorageRecoveryState.StorageUnavailable, presentation.State);
        Assert.True(presentation.BlocksBoot);
        Assert.False(presentation.CanExport);
        Assert.Equal(
            StorageRecoveryActions.Retry | StorageRecoveryActions.DiagnoseReadOnly,
            presentation.Actions);
    }

    [Fact]
    public void Invalid_backup_never_offers_replace_or_reset_directly()
    {
        StorageRecoveryPresentation presentation = BrowserStorageRecoveryClassifier.Classify(
            new IndexedDbBackupValidationException("invalid"),
            StorageRecoveryContext.Restore,
            CorrelationId);

        Assert.Equal(StorageRecoveryState.BackupValidationFailed, presentation.State);
        Assert.False(presentation.BlocksBoot);
        Assert.True(presentation.CanExport);
        Assert.True(presentation.Actions.HasFlag(StorageRecoveryActions.RestoreMerge));
        Assert.False(presentation.Actions.HasFlag(StorageRecoveryActions.RestoreReplace));
        Assert.False(presentation.Actions.HasFlag(StorageRecoveryActions.Reset));
    }

    [Theory]
    [InlineData(StorageRecoveryActions.RestoreReplace, "REPLACE")]
    [InlineData(StorageRecoveryActions.Reset, "RESET")]
    public void Destructive_actions_require_exact_targeted_phrase(
        StorageRecoveryActions action,
        string phrase)
    {
        StorageRecoveryPresentation presentation =
            BrowserStorageRecoveryClassifier.AwaitDestructiveConfirmation(
                action,
                "All local Kazo OS profiles and files",
                CorrelationId);

        Assert.Equal(StorageRecoveryState.AwaitingDestructiveConfirmation, presentation.State);
        Assert.True(presentation.BlocksBoot);
        Assert.True(presentation.CanExport);
        StorageDestructiveConfirmation confirmation = Assert.IsType<StorageDestructiveConfirmation>(
            presentation.Confirmation);
        Assert.True(confirmation.IsConfirmedBy(phrase));
        Assert.False(confirmation.IsConfirmedBy(phrase.ToLowerInvariant()));
        Assert.False(confirmation.IsConfirmedBy($" {phrase}"));
        Assert.Equal(action | StorageRecoveryActions.Export, presentation.Actions);
    }
}