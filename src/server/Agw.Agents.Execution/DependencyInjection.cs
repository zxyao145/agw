using Agw.Agents.Contracts.Catalog;
using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Agentflows.Context;
using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Agents.Execution.Agentflows.Runners.Durable;
using Agw.Agents.Execution.Agentflows.Runners.InProcess;
using Agw.Agents.Execution.Agentflows.Runtime;
using Agw.Agents.Execution.Agentflows.Workflows;
using Agw.Agents.Execution.Agents.Composition;
using Agw.Agents.Execution.Agents.Context.Workspace;
using Agw.Agents.Execution.Agents.Middleware;
using Agw.Agents.Execution.Agents.Runners.Durable;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Sessions;
using Agw.Agents.Execution.Commands;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.HumanInteraction;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Inbound.Facades;
using Agw.Agents.Execution.Inbound.SignalR;
using Agw.Agents.Execution.Messaging.Durable;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Runtimes.Durable;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Agents.Execution.Runtimes.InProcess;
using Agw.Agents.Execution.Summaries;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Agw.Agents.Execution;

/// <summary>
/// 注册 Agent 执行运行时与其传输边界。
/// </summary>
public static class DependencyInjection
{
    public sealed record RegistrationOptions(
        bool AddExecutionTransport = true,
        bool AddDistributedWorker = true,
        bool AddTraceCollector = true,
        bool AddRuntime = true
    );

    /// <summary>
    /// 根据配置注册 InProcess 或 Distributed execution 实现。
    /// </summary>
    public static IServiceCollection AddAgentExecution(
        this IServiceCollection services,
        IConfiguration configuration,
        RegistrationOptions? registrationOptions = null
    )
    {
        registrationOptions ??= new RegistrationOptions();
        var executionOptions =
            configuration.GetSection(ExecutionRuntimeOptions.SectionName).Get<ExecutionRuntimeOptions>()
            ?? new ExecutionRuntimeOptions();
        services.Configure<ExecutionRuntimeOptions>(configuration.GetSection(ExecutionRuntimeOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IAgentflowMermaidProvider, AgentflowMermaidProvider>();
        services.AddSingleton<AgentflowCheckpointStore>();
        if (!registrationOptions.AddRuntime && executionOptions.Provider != ExecutionProvider.Distributed)
            throw new AgwException(
                ErrorCodes.DurableExecutionUnavailable,
                "A submission-only Host requires Distributed execution."
            );
        if (registrationOptions.AddRuntime)
        {
            services.AddSingleton<IAgentInstructionsSource, ProjectInstructionsSource>();
            services.AddScoped<AgentflowWorkflowFactory>();
            services.AddScoped<AgentflowExecutionContextFactory>();
            services.AddScoped<AgentflowCheckpointSupport>();
            services.AddScoped<DurableAgentflowSegmentRunner>();
            services.AddScoped<InProcessAgentflowRunner>();
            services.AddScoped<AgentflowRuntimeService>();
            services.AddScoped<IAgentflowRuntimeService>(provider =>
                provider.GetRequiredService<AgentflowRuntimeService>()
            );
            services.TryAddSingleton(TimeProvider.System);
            services.AddScoped<AgentSessionStateStore>();
            services.AddScoped<AgentCapabilityComposer>();
            services.AddScoped<AgentTurnExecutor>();
            services.AddScoped<ExternalProviderSessionBindings>();
            services.AddScoped<AgentRuntimeConfiguration>();
            services.AddScoped<AgentRuntimeService>();
            services.AddScoped<IAgentRuntimeService>(serviceProvider =>
                serviceProvider.GetRequiredService<AgentRuntimeService>()
            );
        }
        if (executionOptions.Provider == ExecutionProvider.Distributed)
            services.AddScoped<IAgentExecutionRunner, DurableAgentExecutionRunner>();
        else
            services.AddScoped<IAgentExecutionRunner, InProcessAgentExecutionRunner>();
        services.AddScoped<AgentExecutionFacade>();
        services.AddScoped<IAgentExecutionFacade>(provider => provider.GetRequiredService<AgentExecutionFacade>());
        services.AddScoped<IDurableAgentExecutionFacade>(provider =>
            provider.GetRequiredService<AgentExecutionFacade>()
        );
        if (registrationOptions.AddRuntime)
        {
            services.AddScoped<ISummaryChatClientFactory, SummaryChatClientFactory>();
            services.AddScoped<IAgentTurnSummaryService, AgentTurnSummaryService>();
            services.AddScoped<IRuntimeFactory, RuntimeFactory>();
            if (registrationOptions.AddExecutionTransport)
            {
                services.AddExecutionCommands();
                services.AddScoped<ExecutionCommandDispatcher>();
                services.AddScoped<ExecutionConnectionContextFactory>();
                services.AddSingleton<ExecutionConnectionRegistry>();
            }
            services.AddSingleton<RuntimeTurnContextAccessor>();
            services.AddSingleton<IRuntimeTurnContextAccessor>(provider =>
                provider.GetRequiredService<RuntimeTurnContextAccessor>()
            );
            services.AddSingleton<ICurrentAgentTurn>(provider =>
                provider.GetRequiredService<RuntimeTurnContextAccessor>()
            );
            services.AddSingleton<HumanInteractionContextAccessor>();
            services.AddSingleton<IHumanInteractionContextAccessor>(serviceProvider =>
                serviceProvider.GetRequiredService<HumanInteractionContextAccessor>()
            );
            services.AddSingleton<ObservabilityMiddleware>();
            services.AddSingleton<UsageTrackingMiddleware>();
            services.AddSingleton<IAgentflowNodeExecutionTraceStore, AgentflowNodeExecutionTraceStore>();
            if (registrationOptions.AddTraceCollector)
            {
                services.AddSingleton<AgentflowNodeExecutionTraceCollector>();
                services.AddHostedService(serviceProvider =>
                    serviceProvider.GetRequiredService<AgentflowNodeExecutionTraceCollector>()
                );
            }
        }

        if (executionOptions.Provider == ExecutionProvider.Distributed)
        {
            ValidateDistributedConfiguration(configuration, executionOptions);
            services.AddScoped<DurableExecutionStore>();
            if (registrationOptions.AddRuntime)
            {
                services.AddScoped<DurableAgentSegmentRunner>();
                services.AddScoped<DurableExecutionSegmentExecutor>();
                services.AddScoped<IDurableExecutionSegmentExecutor>(sp =>
                    sp.GetRequiredService<DurableExecutionSegmentExecutor>()
                );
            }
            AddExecutionEventStream(services, executionOptions);
            services.AddSingleton<DurableExecutionCoordinator>();
            services.AddSingleton<IDurableExecutionClient, DurableExecutionClient>();
            if (registrationOptions.AddRuntime && registrationOptions.AddDistributedWorker)
            {
                services.AddHostedService<DistributedExecutionWorker>();
            }
        }

        return services;
    }

    /// <summary>
    /// 按 distributed event stream provider 注册 PostgreSQL 或 Redis Stream 实现。
    /// </summary>
    private static void AddExecutionEventStream(IServiceCollection services, ExecutionRuntimeOptions options)
    {
        if (options.Distributed.EventStream.Provider == ExecutionEventStreamProvider.Postgres)
        {
            services.AddSingleton<IExecutionEventStream, PostgresExecutionEventStream>();
            return;
        }

        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var redisOptions = ConfigurationOptions.Parse(options.Distributed.EventStream.Redis.ConnectionString);
            redisOptions.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(redisOptions);
        });
        services.AddSingleton<IExecutionEventStream, RedisExecutionEventStream>();
    }

    /// <summary>
    /// 在应用启动阶段验证 distributed execution 依赖，避免请求运行后才暴露不完整配置。
    /// </summary>
    private static void ValidateDistributedConfiguration(IConfiguration configuration, ExecutionRuntimeOptions options)
    {
        var databaseProvider = configuration["Database:Provider"] ?? "sqlite";
        if (!string.Equals(databaseProvider, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionUnavailable,
                "Execution:Provider=Distributed requires Database:Provider=postgres."
            );
        }
        var distributedLockProvider = configuration["DistributedLock:Provider"];
        if (
            !string.IsNullOrWhiteSpace(distributedLockProvider)
            && !string.Equals(distributedLockProvider, "postgres", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionUnavailable,
                "Execution:Provider=Distributed requires DistributedLock:Provider=postgres or an empty value that follows the PostgreSQL database provider."
            );
        }
        var eventStream = options.Distributed.EventStream;
        if (!Enum.IsDefined(eventStream.Provider))
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionUnavailable,
                $"Execution event stream provider '{eventStream.Provider}' is not supported."
            );
        }
        if (
            eventStream.Provider == ExecutionEventStreamProvider.Redis
            && string.IsNullOrWhiteSpace(eventStream.Redis.ConnectionString)
        )
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionUnavailable,
                "Execution:Distributed:EventStream:Redis:ConnectionString is required when the event stream provider is Redis."
            );
        }
        if (
            options.Distributed.WorkerPollingMilliseconds <= 0
            || options.Distributed.MaxConcurrentExecutions <= 0
            || options.Distributed.RecoveryProbeSeconds <= 0
            || options.Distributed.LockAcquireTimeoutMilliseconds <= 0
            || eventStream.ReadPollingMilliseconds <= 0
            || eventStream.ReadBatchSize <= 0
            || eventStream.WriteIntervalMilliseconds < 0
            || eventStream.WriteBatchSize <= 0
            || (eventStream.Provider == ExecutionEventStreamProvider.Redis && eventStream.Redis.StreamTtlMinutes <= 0)
        )
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionUnavailable,
                "Distributed execution worker, lock, event stream polling, batch, and Redis TTL settings must be positive."
            );
        }
    }
}
