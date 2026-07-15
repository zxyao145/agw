using Agw.Infrastructure;
using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Jobs;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Shared.Configuration;
using Agw.Shared.Exceptions;

using Medallion.Threading;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agw.Jobs.Tests;

public class InfrastructureRegistrationTests
{
    [Fact]
    public void DistributedLockProvider_BelongsToInfrastructureConfiguration()
    {
        Assert.Equal("Agw.Infrastructure.Configuration", typeof(DistributedLockProvider).Namespace);
    }

    [Fact]
    public void AddInfrastructure_RegistersProjectExecutionLockRouter()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Provider"] = "sqlite" })
            .Build();
        var services = new ServiceCollection();

        services.AddInfrastructure(configuration);

        var descriptor = services.Last(x => x.ServiceType == typeof(IProjectExecutionLock));
        Assert.Equal(typeof(ProjectExecutionLockRouter), descriptor.ImplementationType);
        Assert.Contains(services, x => x.ServiceType == typeof(InMemoryProjectExecutionLock));
        Assert.Contains(services, x =>
            x.ServiceType == typeof(Func<DistributedLockProvider, string, IDistributedLockProvider>));
        Assert.Contains(services, x => x.ServiceType == typeof(IConfigureOptions<DistributedLockSettings>));
    }

    [Fact]
    public void AddInfrastructure_BindsDatabaseSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "postgres",
                ["Database:ConnectionString"] = "Host=database"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddInfrastructure(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var settings = serviceProvider.GetRequiredService<IOptions<DatabaseSettings>>().Value;
        Assert.Equal(DatabaseProvider.Postgres, settings.Provider);
    }

    [Fact]
    public void AddInfrastructure_BindsDistributedLockSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "sqlite",
                ["DistributedLock:Provider"] = "postgres",
                ["DistributedLock:ConnectionString"] = "Host=locks"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddInfrastructure(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var settings = serviceProvider.GetRequiredService<IOptions<DistributedLockSettings>>().Value;
        Assert.Equal(DistributedLockProvider.Postgres, settings.Provider);
        Assert.Equal("Host=locks", settings.ConnectionString);
    }

    [Theory]
    [InlineData("mysql")]
    [InlineData("sqlserver")]
    [InlineData("postgresql")]
    public void AddInfrastructure_WhenDatabaseProviderIsUnsupported_ThrowsAgwException(string provider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = provider
            })
            .Build();
        var services = new ServiceCollection();

        var exception = Assert.Throws<AgwException>(() => services.AddInfrastructure(configuration));

        Assert.Equal(ErrorCodes.UnsupportedDatabaseProvider.Code, exception.Code);
    }

    [Theory]
    [InlineData("redis")]
    [InlineData("postgresql")]
    [InlineData("")]
    public void AddInfrastructure_WhenDistributedLockProviderIsUnsupported_ThrowsAgwException(string provider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "postgres",
                ["Database:ConnectionString"] = "Host=database",
                ["DistributedLock:Provider"] = provider
            })
            .Build();
        var services = new ServiceCollection();

        var exception = Assert.Throws<AgwException>(() => services.AddInfrastructure(configuration));

        Assert.Equal(ErrorCodes.UnsupportedDistributedLockProvider.Code, exception.Code);
    }
}
