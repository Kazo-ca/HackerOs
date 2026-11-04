using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.AppSdk.Blazor.Interop;
using Kazo.Os.Infrastructure.Browser.Backup;
using Kazo.Os.Platform.Core.Backup;
using Microsoft.JSInterop;

namespace Kazo.Os.Ecosystem.Administration;

/// <summary>
/// The browser as the backup "hardware" of <see cref="SystemBackupService"/> (ADR 0099, section 1):
/// <see cref="IndexedDbBackupService"/> exports and restores the OS database, the SDK Blazor
/// <c>download.js</c> hands the archive to the host computer and <c>pickFile.js</c> opens its file
/// picker. The restore record survives the restart in <c>localStorage</c>, outside the database the
/// restore overwrites.
/// </summary>
internal sealed class BrowserSystemBackupDevice(IJSRuntime runtime) : ISystemBackupDevice
{
    /// <summary><c>localStorage</c> key of the restore record kept across the restart.</summary>
    internal const string RestoreRecordKey = "hackeros.backup.restore-record";

    /// <inheritdoc />
    public async Task<SystemBackupArchive> ExportAsync(CancellationToken cancellationToken = default)
    {
        await using IndexedDbBackupService backup = new(runtime);
        byte[] archive = await backup.ExportArchiveAsync(cancellationToken).ConfigureAwait(false);
        IndexedDbBackupSummary summary = IndexedDbBackupService.Inspect(IndexedDbBackupService.ReadArchive(archive));
        return new SystemBackupArchive(archive, new SystemBackupSummary(summary.CreatedAtUtc, summary.RecordCount));
    }

    /// <inheritdoc />
    public async Task SaveToHostAsync(string fileName, byte[] archive, CancellationToken cancellationToken = default)
    {
        await using BrowserFileDownloader downloader = new(runtime);
        using MemoryStream content = new(archive, writable: false);
        await downloader.DownloadFileFromStreamAsync(fileName, content).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SystemBackupFile?> PickFromHostAsync(CancellationToken cancellationToken = default)
    {
        await using BrowserFilePicker picker = new(runtime);
        BrowserPickedFile? file = await picker.PickAsync(
            IndexedDbBackupService.ArchiveExtension + ",.json,application/json,application/gzip",
            cancellationToken).ConfigureAwait(false);
        return file is null ? null : new SystemBackupFile(file.Name, file.Content);
    }

    /// <inheritdoc />
    public SystemBackupSummary Inspect(byte[] archive)
    {
        try
        {
            IndexedDbBackupSummary summary = IndexedDbBackupService.Inspect(IndexedDbBackupService.ReadArchive(archive));
            return new SystemBackupSummary(summary.CreatedAtUtc, summary.RecordCount);
        }
        catch (IndexedDbBackupValidationException exception)
        {
            throw new HackBackupInvalidException(exception.Message, exception);
        }
    }

    /// <inheritdoc />
    public async Task RestoreAsync(byte[] archive, HackBackupRestoreMode mode, CancellationToken cancellationToken = default)
    {
        await using IndexedDbBackupService backup = new(runtime);
        await backup.RestoreAsync(
            IndexedDbBackupService.ReadArchive(archive),
            mode == HackBackupRestoreMode.Replace ? IndexedDbRestoreMode.Replace : IndexedDbRestoreMode.Merge,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveRestoreRecordAsync(SystemRestoreRecord record, CancellationToken cancellationToken = default) =>
        await runtime.InvokeVoidAsync(
            "localStorage.setItem", cancellationToken, RestoreRecordKey, JsonSerializer.Serialize(record, RestoreRecordJsonContext.Default.SystemRestoreRecord))
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<SystemRestoreRecord?> TakeRestoreRecordAsync(CancellationToken cancellationToken = default)
    {
        string? json = await runtime.InvokeAsync<string?>("localStorage.getItem", cancellationToken, RestoreRecordKey)
            .ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        await runtime.InvokeVoidAsync("localStorage.removeItem", cancellationToken, RestoreRecordKey).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize(json, RestoreRecordJsonContext.Default.SystemRestoreRecord);
        }
        catch (JsonException)
        {
            // A damaged record is dropped: it must never block the boot.
            return null;
        }
    }
}

/// <summary>Trim-safe JSON of the restore record kept in <c>localStorage</c>.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SystemRestoreRecord))]
internal sealed partial class RestoreRecordJsonContext : JsonSerializerContext;
