using Microsoft.Data.Sqlite;
using Xunit;

namespace Agw.Host.Tests;

public sealed class SqliteRuntimeTests
{
    [Fact]
    public async Task OpenConnection_BundledNativeLibrary_ContainsAggregateBoundsFix()
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";

        var actual = Version.Parse(Assert.IsType<string>(await command.ExecuteScalarAsync(token)));

        Assert.True(actual >= new Version(3, 50, 2), $"SQLite {actual} predates the CVE-2025-6965 fix.");
    }
}
