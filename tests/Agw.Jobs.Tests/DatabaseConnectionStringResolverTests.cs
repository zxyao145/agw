using System.Data.Common;

using Agw.Infrastructure.Configuration;
using Agw.Shared.Configuration;
using Agw.Shared.Runtime;

namespace Agw.Jobs.Tests;

public class DatabaseConnectionStringResolverTests
{
    [Fact]
    public void Resolve_WhenSqliteDataSourceIsRelative_UsesDatabaseDirectory()
    {
        var paths = AgwDataPaths.Resolve("/tmp/agw-data", "/unused");

        var result = DatabaseConnectionStringResolver.Resolve(
            DatabaseProvider.Sqlite,
            "Data Source=custom.db",
            paths);

        Assert.Equal($"Data Source={Path.Combine(paths.Root, "database", "custom.db")}", result);
    }

    [Fact]
    public void Resolve_WhenProviderIsNotSqlite_PreservesConnectionString()
    {
        const string connectionString = "Host=db;Database=agw";
        var paths = AgwDataPaths.Resolve("/tmp/agw-data", "/unused");

        var result = DatabaseConnectionStringResolver.Resolve(
            DatabaseProvider.Postgres,
            connectionString,
            paths);

        Assert.Equal(connectionString, result);
    }

    [Fact]
    public void ToSafeLogValue_WhenConnectionStringContainsPassword_RedactsPassword()
    {
        const string connectionString =
            "Host=db;Database=agw;Username=postgres;Password=super-secret;Application Name=agw-server";

        var result = DatabaseConnectionStringResolver.ToSafeLogValue(connectionString);
        var builder = new DbConnectionStringBuilder
        {
            ConnectionString = result
        };

        Assert.Equal("db", builder["Host"]);
        Assert.Equal("agw", builder["Database"]);
        Assert.Equal("***", builder["Password"]);
        Assert.DoesNotContain("super-secret", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Pwd")]
    [InlineData("SSL Password")]
    [InlineData("Access Token")]
    [InlineData("Client Secret")]
    public void ToSafeLogValue_WhenConnectionStringContainsSensitiveAlias_RedactsValue(string key)
    {
        var result = DatabaseConnectionStringResolver.ToSafeLogValue($"Host=db;{key}=super-secret");
        var builder = new DbConnectionStringBuilder
        {
            ConnectionString = result
        };

        Assert.Equal("***", builder[key]);
        Assert.DoesNotContain("super-secret", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ToSafeLogValue_WhenConnectionStringIsInvalid_ReturnsSafePlaceholder()
    {
        var result = DatabaseConnectionStringResolver.ToSafeLogValue("Password=secret;broken");

        Assert.Equal("<invalid connection string>", result);
        Assert.DoesNotContain("secret", result, StringComparison.Ordinal);
    }

}
