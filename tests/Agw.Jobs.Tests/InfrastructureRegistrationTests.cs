using Agw.Infrastructure;
using Agw.Infrastructure.Jobs;
using Agw.Jobs.External;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Jobs.Tests;

public class InfrastructureRegistrationTests
{
    [Fact]
    public void AddInfrastructure_WhenRedisIsDisabled_UsesInMemoryProjectLock()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Redis:Enabled"] = "false" })
            .Build();
        var services = new ServiceCollection();

        services.AddInfrastructure(configuration);

        var descriptor = services.Last(x => x.ServiceType == typeof(IProjectExecutionLock));
        Assert.Equal(typeof(InMemoryProjectExecutionLock), descriptor.ImplementationType);
        Assert.DoesNotContain(services, x => x.ServiceType == typeof(StackExchange.Redis.IConnectionMultiplexer));
    }

    [Fact]
    public void AddInfrastructure_WhenRedisIsEnabled_UsesRedisProjectLock()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Redis:Enabled"] = "true",
                ["Redis:ConnectionString"] = "localhost:6379,abortConnect=false"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddInfrastructure(configuration);

        var descriptor = services.Last(x => x.ServiceType == typeof(IProjectExecutionLock));
        Assert.Equal(typeof(RedisProjectExecutionLock), descriptor.ImplementationType);
        Assert.Contains(services, x => x.ServiceType == typeof(StackExchange.Redis.IConnectionMultiplexer));
    }
}
