using Kazo.Os.Server.Data;
using Xunit;

namespace Kazo.Os.Server.Tests;

/// <summary>ADR 0230: the database of a server installed before 3.0.0 is renamed at start.</summary>
public sealed class LegacyDatabaseFileTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("kazoos-legacy-db-").FullName;

    [Fact]
    public void The_old_database_and_its_companions_become_kazoos_db_when_it_is_missing()
    {
        File.WriteAllText(Path.Combine(_folder, "hackeros.db"), "accounts");
        File.WriteAllText(Path.Combine(_folder, "hackeros.db-wal"), "log");

        string? moved = LegacyDatabaseFile.MoveIfNeeded($"Data Source={Path.Combine(_folder, "kazoos.db")}", "/");

        Assert.Equal(Path.Combine(_folder, "kazoos.db"), moved);
        Assert.Equal("accounts", File.ReadAllText(Path.Combine(_folder, "kazoos.db")));
        Assert.Equal("log", File.ReadAllText(Path.Combine(_folder, "kazoos.db-wal")));
        Assert.False(File.Exists(Path.Combine(_folder, "hackeros.db")));
    }

    [Fact]
    public void A_relative_data_source_is_resolved_against_the_given_folder()
    {
        File.WriteAllText(Path.Combine(_folder, "hackeros.db"), "accounts");

        Assert.NotNull(LegacyDatabaseFile.MoveIfNeeded("Data Source=kazoos.db", _folder));
        Assert.True(File.Exists(Path.Combine(_folder, "kazoos.db")));
    }

    [Fact]
    public void An_existing_kazoos_db_is_never_replaced()
    {
        File.WriteAllText(Path.Combine(_folder, "hackeros.db"), "old");
        File.WriteAllText(Path.Combine(_folder, "kazoos.db"), "new");

        Assert.Null(LegacyDatabaseFile.MoveIfNeeded("Data Source=kazoos.db", _folder));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_folder, "kazoos.db")));
        Assert.True(File.Exists(Path.Combine(_folder, "hackeros.db")));
    }

    [Theory]
    [InlineData("Data Source=accounts.db")]
    [InlineData("Data Source=:memory:")]
    [InlineData("not a connection string")]
    public void Another_database_name_moves_nothing(string connectionString)
    {
        File.WriteAllText(Path.Combine(_folder, "hackeros.db"), "old");

        Assert.Null(LegacyDatabaseFile.MoveIfNeeded(connectionString, _folder));
        Assert.True(File.Exists(Path.Combine(_folder, "hackeros.db")));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
