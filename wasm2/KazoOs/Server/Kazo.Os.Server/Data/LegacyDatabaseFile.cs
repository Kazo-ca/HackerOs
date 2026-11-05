using Microsoft.Data.Sqlite;

namespace Kazo.Os.Server.Data;

/// <summary>
/// ADR 0230: a server installed before 3.0.0 kept its accounts in <c>hackeros.db</c> (a Docker
/// volume, an installed data folder). When the configured database is <c>kazoos.db</c> and does
/// not exist yet while <c>hackeros.db</c> sits next to it, the old file (with its <c>-wal</c> and
/// <c>-shm</c> companions) is renamed before the server opens it.
/// </summary>
public static class LegacyDatabaseFile
{
    /// <summary>The database file name before the rename.</summary>
    public const string LegacyFileName = "hackeros.db";

    /// <summary>The database file name since the rename.</summary>
    public const string FileName = "kazoos.db";

    private static readonly string[] Companions = ["", "-wal", "-shm", "-journal"];

    /// <summary>Renames <c>hackeros.db</c> to the configured <c>kazoos.db</c> when only the old one exists.</summary>
    /// <param name="connectionString">The SQLite connection string the server uses.</param>
    /// <param name="baseDirectory">The folder a relative data source is resolved against (the working directory, as SQLite does).</param>
    /// <returns>The path of the moved database, or <see langword="null"/> when nothing moved.</returns>
    public static string? MoveIfNeeded(string connectionString, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentNullException.ThrowIfNull(baseDirectory);
        string dataSource;
        try
        {
            dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(dataSource) || dataSource == ":memory:"
            || !string.Equals(Path.GetFileName(dataSource), FileName, StringComparison.Ordinal))
        {
            return null;
        }

        string target = Path.GetFullPath(dataSource, baseDirectory);
        string legacy = Path.Combine(Path.GetDirectoryName(target)!, LegacyFileName);
        if (File.Exists(target) || !File.Exists(legacy))
        {
            return null;
        }

        foreach (string suffix in Companions)
        {
            if (File.Exists(legacy + suffix))
            {
                File.Move(legacy + suffix, target + suffix);
            }
        }

        return target;
    }
}
