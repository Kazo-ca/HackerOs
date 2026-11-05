using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Settings;
using Kazo.Os.Simulation.Abstractions.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Platform.Core.Apps;

/// <summary>
/// Installs <c>/etc/kazoos/apps.config</c> and copies the retired ADR 0032 enablement flags into
/// it, once (ADR 0048 section 6).
/// </summary>
/// <remarks>
/// The ADR 0032 disable was machine-wide, so every <c>IsEnabled = false</c> record of a non-system
/// app becomes a <c>[uninstalled]</c> line of the machine file. Runs at boot with an audited system
/// context, before <see cref="AppInstallationService.StartAsync"/>. Marker:
/// <c>/var/lib/kazoos/migrations/app-enablement-to-files</c>. The IndexedDB store and its schema
/// version are left untouched.
/// </remarks>
public sealed class AppEnablementToFilesMigration
{
    /// <summary>Migration marker.</summary>
    public const string MarkerPath = "/var/lib/kazoos/migrations/app-enablement-to-files";

    private readonly IFileSystemService _fileSystem;
    private readonly ISimulationClock _clock;
    private readonly AppCatalog _catalog;
    private readonly ILogger _logger;

    /// <summary>Creates the migration.</summary>
    /// <param name="fileSystem">Filesystem engine.</param>
    /// <param name="clock">Simulation clock.</param>
    /// <param name="catalog">Catalog of this build, used to skip system apps.</param>
    /// <param name="logger">Optional logger.</param>
    public AppEnablementToFilesMigration(
        IFileSystemService fileSystem,
        ISimulationClock clock,
        AppCatalog catalog,
        ILogger<AppEnablementToFilesMigration>? logger = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Writes the commented machine file when absent, then, unless the marker exists, appends the
    /// disabled apps of <paramref name="entries"/> to it and writes the marker. Never fails boot:
    /// a write error is logged and the migration retried at the next boot.
    /// </summary>
    /// <param name="entries">Persisted catalog entries, as reconciled at boot.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public async ValueTask MigrateAsync(IEnumerable<PersistedAppCatalogEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        IAppFileSystemGateway system = new AppFileSystemGateway(_fileSystem, _clock, OsSettingsService.CreateSystemOperation(), ["system"]);

        if (await ExistsAsync(system, VirtualPath.Parse(MarkerPath), cancellationToken).ConfigureAwait(false))
        {
            // Already migrated: only restore the documented file if an administrator deleted it.
            if (!await ExistsAsync(system, AppsConfigDocument.MachinePath, cancellationToken).ConfigureAwait(false))
            {
                await EditOrLogAsync(system, AppsConfigDocument.MachinePath,
                    static content => content ?? AppsConfigDocument.MachineDefaultContent, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        string[] disabled = [.. entries
            .Where(entry => !entry.IsEnabled && !_catalog.IsSystemApp(entry.Manifest.Id))
            .Select(entry => entry.Manifest.Id)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

        bool written = await EditOrLogAsync(
            system,
            AppsConfigDocument.MachinePath,
            content => AppsConfigDocument.MarkUninstalled(content, disabled, AppsConfigDocument.MachineDefaultContent),
            cancellationToken).ConfigureAwait(false);
        if (!written)
        {
            return;
        }

        foreach (string appId in disabled)
        {
            _logger.LogInformation("apps: {AppId} uninstalled (scope=machine, by=migration from the disabled flag)", appId);
        }

        await EditOrLogAsync(system, VirtualPath.Parse(MarkerPath), static content => content ?? string.Empty, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> EditOrLogAsync(
        IAppFileSystemGateway gateway,
        VirtualPath path,
        Func<string?, string> edit,
        CancellationToken cancellationToken)
    {
        FileSystemError? error = await SettingsTextFile.EditAsync(gateway, path, edit, createIfMissing: true, cancellationToken).ConfigureAwait(false);
        if (error is null)
        {
            return true;
        }

        _logger.LogWarning("apps: cannot write {Path}: {Code}, migration retried at next boot", path.Value, error.Code);
        return false;
    }

    private static async ValueTask<bool> ExistsAsync(IAppFileSystemGateway gateway, VirtualPath path, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemEntrySnapshot> stat = await gateway
            .StatAsync(new FileSystemStatRequest(path), cancellationToken)
            .ConfigureAwait(false);
        return stat.Succeeded;
    }
}
