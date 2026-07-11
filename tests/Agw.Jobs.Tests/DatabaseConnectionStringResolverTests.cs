using Agw.Infrastructure.Configuration;
using Agw.Shared.Runtime;

namespace Agw.Jobs.Tests;

public class DatabaseConnectionStringResolverTests
{
    [Fact]
    public void Resolve_WhenSqliteDataSourceIsRelative_UsesDatabaseDirectory()
    {
        var paths = AgwDataPaths.Resolve("/tmp/agw-data", "/unused");

        var result = DatabaseConnectionStringResolver.Resolve("sqlite", "Data Source=custom.db", paths);

        Assert.Equal($"Data Source={Path.Combine(paths.Root, "database", "custom.db")}", result);
    }

    [Fact]
    public void Resolve_WhenProviderIsNotSqlite_PreservesConnectionString()
    {
        const string connectionString = "Host=db;Database=agw";
        var paths = AgwDataPaths.Resolve("/tmp/agw-data", "/unused");

        var result = DatabaseConnectionStringResolver.Resolve("postgres", connectionString, paths);

        Assert.Equal(connectionString, result);
    }
}
