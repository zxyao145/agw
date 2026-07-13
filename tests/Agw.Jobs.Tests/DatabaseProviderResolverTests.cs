using Agw.Infrastructure.Configuration;
using Agw.Shared.Configuration;
using Agw.Shared.Exceptions;

namespace Agw.Jobs.Tests;

public class DatabaseProviderResolverTests
{
    [Theory]
    [InlineData("sqlite", DatabaseProvider.Sqlite)]
    [InlineData(" SQLite ", DatabaseProvider.Sqlite)]
    [InlineData("postgres", DatabaseProvider.Postgres)]
    [InlineData(" POSTGRES ", DatabaseProvider.Postgres)]
    public void Parse_WhenProviderIsSupported_ReturnsProvider(string provider, DatabaseProvider expected)
    {
        var result = DatabaseProviderResolver.Parse(provider);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("mysql")]
    [InlineData("sqlserver")]
    [InlineData("postgresql")]
    [InlineData("")]
    public void Parse_WhenProviderIsUnsupported_ThrowsAgwException(string provider)
    {
        var exception = Assert.Throws<AgwException>(() => DatabaseProviderResolver.Parse(provider));

        Assert.Equal(ErrorCodes.UnsupportedDatabaseProvider.Code, exception.Code);
        Assert.Contains(provider, exception.Message, StringComparison.Ordinal);
    }
}
