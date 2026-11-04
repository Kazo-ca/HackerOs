using Microsoft.Data.Sqlite;

namespace Kazo.Os.Server.Services;

/// <summary>
/// Creates and restores SQLite snapshots for trusted server-operator workflows.
/// This service deliberately has no HTTP endpoint: restoring a database is an
/// operator action and must never be delegated to a browser client.
/// </summary>
public interface IServerDatabaseBackupService
{
    /// <summary>Creates a consistent snapshot with a validated operator-provided name.</summary>
    Task<ServerDatabaseBackupResult> CreateAsync(string snapshotName, CancellationToken cancellationToken);

    /// <summary>Restores a previously created snapshot into the configured database.</summary>
    Task RestoreAsync(string snapshotName, CancellationToken cancellationToken);
}

/// <summary>Describes a completed server database snapshot.</summary>
public sealed record ServerDatabaseBackupResult(string Path, long LengthBytes, DateTimeOffset CreatedUtc);

/// <inheritdoc />
public sealed class ServerDatabaseBackupService : IServerDatabaseBackupService
{
    private readonly string _connectionString;
    private readonly string _backupRoot;

    /// <summary>Initializes the service from the server's deployment configuration.</summary>
    public ServerDatabaseBackupService(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _connectionString = configuration.GetConnectionString("KazoOsDb")
            ?? throw new InvalidOperationException("ConnectionStrings:KazoOsDb must be configured for database backup.");
        _backupRoot = Path.GetFullPath(configuration["ServerBackup:Root"] ?? "backups");
    }

    /// <inheritdoc />
    public async Task<ServerDatabaseBackupResult> CreateAsync(string snapshotName, CancellationToken cancellationToken)
    {
        string snapshotPath = ResolveSnapshotPath(snapshotName);
        Directory.CreateDirectory(_backupRoot);

        await using var source = new SqliteConnection(_connectionString);
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new SqliteConnection($"Data Source={snapshotPath}");
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);

        FileInfo snapshot = new(snapshotPath);
        return new ServerDatabaseBackupResult(snapshot.FullName, snapshot.Length, DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task RestoreAsync(string snapshotName, CancellationToken cancellationToken)
    {
        string snapshotPath = ResolveSnapshotPath(snapshotName);
        if (!File.Exists(snapshotPath))
        {
            throw new FileNotFoundException("The requested server database snapshot does not exist.", snapshotPath);
        }

        await using var source = new SqliteConnection($"Data Source={snapshotPath};Mode=ReadOnly");
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new SqliteConnection(_connectionString);
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
    }

    private string ResolveSnapshotPath(string snapshotName)
    {
        if (string.IsNullOrWhiteSpace(snapshotName)
            || !string.Equals(snapshotName, Path.GetFileName(snapshotName), StringComparison.Ordinal)
            || !snapshotName.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Snapshot names must be simple .db file names.", nameof(snapshotName));
        }

        string candidate = Path.GetFullPath(Path.Combine(_backupRoot, snapshotName));
        string rootWithSeparator = _backupRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _backupRoot
            : _backupRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Snapshot path escapes the configured backup root.", nameof(snapshotName));
        }

        return candidate;
    }
}
