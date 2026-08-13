using Agw.Setup.Contracts;
using Agw.Setup.Services;
using Agw.Shared.Configuration;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;

using Microsoft.Data.Sqlite;

using Npgsql;

using Xunit;

namespace Agw.Setup.Tests;

public class SetupConnectionStringFactoryTests
{
    [Fact]
    public void Create_WithRelativeSqlitePath_ResolvesBelowServerDatabaseDirectory()
    {
        var paths = CreatePaths();
        var request = new SetupRequest
        {
            Provider = DatabaseProvider.Sqlite,
            SqlitePath = "custom.db"
        };

        var connectionString = SetupConnectionStringFactory.Create(request, paths);
        var builder = new SqliteConnectionStringBuilder(connectionString);

        Assert.Equal(Path.Combine(paths.Root, "database", "custom.db"), builder.DataSource);
    }

    [Fact]
    public void Create_WithPostgresFields_EscapesValuesAndPreservesPassword()
    {
        var request = new SetupRequest
        {
            Provider = DatabaseProvider.Postgres,
            PostgresHost = " 2001:db8::1 ",
            PostgresPort = 5544,
            PostgresDatabase = " agent;gateway ",
            PostgresUsername = " agw-user ",
            PostgresPassword = "p;ass=\"word"
        };

        var connectionString = SetupConnectionStringFactory.Create(request, CreatePaths());
        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        Assert.Equal("2001:db8::1", builder.Host);
        Assert.Equal(5544, builder.Port);
        Assert.Equal("agent;gateway", builder.Database);
        Assert.Equal("agw-user", builder.Username);
        Assert.Equal("p;ass=\"word", builder.Password);
        Assert.Equal("agw-server", builder.ApplicationName);
    }

    [Fact]
    public void Create_WithUnsupportedProvider_ThrowsSharedError()
    {
        var request = new SetupRequest { Provider = (DatabaseProvider)99 };

        var exception = Assert.Throws<AgwException>(() =>
            SetupConnectionStringFactory.Create(request, CreatePaths()));

        Assert.Equal(ErrorCodes.UnsupportedDatabaseProvider.Code, exception.Code);
    }

    private static AgwDataPaths CreatePaths()
    {
        return AgwDataPaths.Resolve(
            Path.Combine(Path.GetTempPath(), $"agw-connection-{Guid.CreateVersion7():N}"),
            "/unused");
    }
}
