using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agents.Store;
using Agw.Agents.Execution.Durable;
using Agw.Agents.ExternalAgents.Pi;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Tests;

public sealed class ExecutionRuntimeConfigurationTests
{
    [Fact]
    public void AddAgents_RegistersCheckpointAndSessionStoresWithExpectedLifetimes()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddAgents(new ConfigurationBuilder().Build());

        // Assert
        Assert.Contains(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(AgentflowCheckpointStore)
                && descriptor.Lifetime == ServiceLifetime.Singleton
        );
        Assert.Contains(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(AgentSessionStateStore)
                && descriptor.Lifetime == ServiceLifetime.Scoped
        );
    }

    [Fact]
    public void AddAgents_DefaultConfiguration_UsesInProcessProvider()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddAgents(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Equal(
            ExecutionProvider.InProcess,
            provider.GetRequiredService<IOptions<ExecutionRuntimeOptions>>().Value.Provider
        );
        Assert.Null(provider.GetService<DurableExecutionCoordinator>());
    }

    [Fact]
    public void AddAgents_PiConfiguration_BindsTrustedExtensionsAndPersistenceTimeout()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ExternalAgents:Pi:Extensions:0"] = "/trusted/extension.ts",
                    ["ExternalAgents:Pi:HistoryPersistenceTimeout"] = "00:00:12",
                }
            )
            .Build();
        var services = new ServiceCollection();

        // Act
        services.AddAgents(configuration);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<PiExternalAgentOptions>>().Value;

        // Assert
        Assert.Equal(["/trusted/extension.ts"], options.Extensions);
        Assert.Equal(TimeSpan.FromSeconds(12), options.HistoryPersistenceTimeout);
    }

    [Fact]
    public void AddAgents_NonPositivePiPersistenceTimeout_FailsValidation()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["ExternalAgents:Pi:HistoryPersistenceTimeout"] = "00:00:00" }
            )
            .Build();
        var services = new ServiceCollection();
        services.AddAgents(configuration);
        using var provider = services.BuildServiceProvider();

        // Act & Assert
        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<PiExternalAgentOptions>>().Value
        );
    }

    [Fact]
    public void AddAgents_DistributedWithSqlite_FailsFast()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Execution:Provider"] = "Distributed",
                    ["Database:Provider"] = "sqlite",
                }
            )
            .Build();

        var exception = Assert.Throws<AgwException>(() => new ServiceCollection().AddAgents(configuration));

        Assert.Equal(ErrorCodes.DurableExecutionUnavailable.Code, exception.Code);
        Assert.Contains("Database:Provider=postgres", exception.Message);
    }

    [Fact]
    public void AddAgents_DistributedWithPostgresEventStream_DoesNotRequireRedis()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Execution:Provider"] = "Distributed",
                    ["Database:Provider"] = "postgres",
                }
            )
            .Build();
        var services = new ServiceCollection();

        services.AddAgents(configuration);

        Assert.Contains(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(IExecutionEventStream)
                && descriptor.ImplementationType == typeof(PostgresExecutionEventStream)
        );
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(StackExchange.Redis.IConnectionMultiplexer)
        );
    }

    [Fact]
    public void AddAgents_RedisEventStreamWithoutConnectionString_FailsFast()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Execution:Provider"] = "Distributed",
                    ["Database:Provider"] = "postgres",
                    ["Execution:Distributed:EventStream:Provider"] = "Redis",
                }
            )
            .Build();

        var exception = Assert.Throws<AgwException>(() => new ServiceCollection().AddAgents(configuration));

        Assert.Equal(ErrorCodes.DurableExecutionUnavailable.Code, exception.Code);
        Assert.Contains("EventStream:Redis:ConnectionString", exception.Message);
    }

    [Fact]
    public void AddAgents_DistributedWithInMemoryLock_FailsFast()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Execution:Provider"] = "Distributed",
                    ["Database:Provider"] = "postgres",
                    ["DistributedLock:Provider"] = "inmemory",
                }
            )
            .Build();

        var exception = Assert.Throws<AgwException>(() => new ServiceCollection().AddAgents(configuration));

        Assert.Equal(ErrorCodes.DurableExecutionUnavailable.Code, exception.Code);
        Assert.Contains("DistributedLock:Provider=postgres", exception.Message);
    }

    [Fact]
    public void AddAgents_DistributedWithInvalidWorkerSettings_FailsFast()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Execution:Provider"] = "Distributed",
                    ["Database:Provider"] = "postgres",
                    ["Execution:Distributed:MaxConcurrentExecutions"] = "0",
                }
            )
            .Build();

        var exception = Assert.Throws<AgwException>(() => new ServiceCollection().AddAgents(configuration));

        Assert.Equal(ErrorCodes.DurableExecutionUnavailable.Code, exception.Code);
        Assert.Contains("must be positive", exception.Message);
    }

    [Fact]
    public void AddAgents_DistributedWithRedisEventStream_RegistersRedisAndWorker()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Execution:Provider"] = "Distributed",
                    ["Database:Provider"] = "postgres",
                    ["Execution:Distributed:EventStream:Provider"] = "Redis",
                    ["Execution:Distributed:EventStream:Redis:ConnectionString"] = "redis:6379",
                }
            )
            .Build();
        var services = new ServiceCollection();

        services.AddAgents(configuration);

        Assert.Contains(services, descriptor => descriptor.ImplementationType == typeof(DistributedExecutionWorker));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(DurableExecutionCoordinator));
        Assert.Contains(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(IExecutionEventStream)
                && descriptor.ImplementationType == typeof(RedisExecutionEventStream)
        );
    }
}
