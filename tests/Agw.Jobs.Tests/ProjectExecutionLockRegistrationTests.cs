using Agw.Infrastructure;
using Agw.Jobs;
using Agw.Jobs.External;
using Agw.Jobs.External.Cluster;
using Agw.Jobs.External.StandAlone;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using StackExchange.Redis;

namespace Agw.Jobs.Tests;

public class ProjectExecutionLockRegistrationTests
{
    [Fact]
    public void AddInfrastructureAndJobs_WhenWorkerPoolModeIsSingleNode_RegistersLocalProjectExecutionLock()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration("SingleNode");

        services.AddInfrastructure(configuration);
        services.AddJobs(configuration);

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IProjectExecutionLock));
        Assert.Equal(typeof(LocalProjectExecutionLock), descriptor.ImplementationType);
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(IConnectionMultiplexer));
    }

    [Fact]
    public void AddInfrastructureAndJobs_WhenWorkerPoolModeIsCluster_RegistersRedisProjectExecutionLock()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration("Cluster");

        services.AddInfrastructure(configuration);
        services.AddJobs(configuration);

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IProjectExecutionLock));
        Assert.Equal(typeof(RedisProjectExecutionLock), descriptor.ImplementationType);
        Assert.Contains(services, item => item.ServiceType == typeof(IConnectionMultiplexer));
    }

    private static IConfiguration CreateConfiguration(string workerPoolMode)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "sqlite",
                ["Database:ConnectionString"] = "Data Source=:memory:",
                ["Jobs:WorkerPool:Mode"] = workerPoolMode
            })
            .Build();
    }
}
