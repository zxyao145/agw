using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Agents.History;
using Agw.Agents.Execution.Agents.Middleware.Telemetry;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Turns;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.Runtimes;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Projects;
using Agw.Projects.Application;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

/// <summary>
/// Durable Segment 中一次真实的 System Agent 运行：telemetry 中间件、会话归属与用量记录器都是生产实现，只有模型调用由 UsageReportingChatClient 代替。
/// One real System Agent run inside a durable segment: the telemetry middleware, session ownership and usage recorder are the production implementations; only the model call is replaced by UsageReportingChatClient.
/// </summary>
internal static class DurableUsageTestServices
{
    public static void Add(IServiceCollection services, UsageReportingChatClient client)
    {
        services.AddScoped<ExecutionContextFactory>();
        services.AddSingleton<HistorySessionState>();
        services.AddSingleton<IProviderSessionState>(provider => provider.GetRequiredService<HistorySessionState>());
        services.AddSingleton<IAgentUsageRecorder, AgentUsageRecorder>();
        services.AddSingleton<AgentTelemetryMiddleware>();
        services.AddScoped(_ => new AgentTurnExecutor(null, null, null, NullLogger<AgentTurnExecutor>.Instance));
        services.AddScoped<IAgentRuntimeFactory>(provider => new UsageAgentRuntimeFactory(
            client,
            provider.GetRequiredService<HistorySessionState>(),
            provider.GetRequiredService<AgentTelemetryMiddleware>()
        ));
    }

    /// <summary>
    /// 写入用户拥有的 System Agent Definition；执行上下文按它确定 Engine 种类。
    /// Writes a System Agent definition owned by the user; the execution context resolves the Engine kind from it.
    /// </summary>
    public static async Task SeedAgentAsync(TurnPersistenceTestKit kit, Guid agentId)
    {
        await using var context = kit.CreateContext();
        context.Agents.Add(
            new Agent
            {
                Id = agentId,
                Name = "usage",
                CreateBy = TurnPersistenceTestKit.UserId,
            }
        );
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 写入一条属于该 Turn 的用量，与 worker 在停止之前写入的用量相同。
    /// Writes one usage record of the turn, like usage a worker wrote before it stopped.
    /// </summary>
    public static async Task AddUsageAsync(
        TurnPersistenceTestKit kit,
        AgentExecutionTask task,
        Guid turnId,
        long totalTokenCount
    )
    {
        await using var context = kit.CreateContext();
        context.AgentUsages.Add(
            new AgentUsage
            {
                Id = Guid.CreateVersion7(),
                ProjectId = task.ProjectId,
                UserId = TurnPersistenceTestKit.UserId,
                ContextId = task.ContextId,
                AgentName = "agent",
                TurnId = turnId,
                RecordedAt = kit.Clock.GetUtcNow(),
                TotalTokenCount = totalTokenCount,
            }
        );
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 经对话详情接口的应用服务读取用量，时间取测试环境的时钟。
    /// Reads the usage through the conversation details application service, timed by the kit's clock.
    /// </summary>
    public static async Task<ProjectConversationUsage> ReadConversationUsageAsync(
        TurnPersistenceTestKit kit,
        AgentExecutionTask task
    )
    {
        await using var context = kit.CreateContext();
        var service = new ProjectConversationAppService(
            context,
            new ProjectResolver(context, new TestUserInfoService(TurnPersistenceTestKit.UserId)),
            new ProjectDeletionCoordinator(
                context,
                kit.Locks,
                new DurableExecutionScopeMaintenance(
                    context,
                    kit.Clock,
                    NullLogger<DurableExecutionScopeMaintenance>.Instance
                ),
                kit.Clock
            ),
            kit.Clock
        );
        var conversation = await service.GetResponseAsync(
            task.ProjectId,
            task.ProjectConversationId,
            TestContext.Current.CancellationToken
        );
        return Assert.IsType<ProjectConversationResponse>(conversation).Usage;
    }

    private sealed class UsageAgentRuntimeFactory : IAgentRuntimeFactory
    {
        private readonly UsageReportingChatClient _client;
        private readonly HistorySessionState _sessionState;
        private readonly AgentTelemetryMiddleware _telemetry;

        public UsageAgentRuntimeFactory(
            UsageReportingChatClient client,
            HistorySessionState sessionState,
            AgentTelemetryMiddleware telemetry
        )
        {
            _client = client;
            _sessionState = sessionState;
            _telemetry = telemetry;
        }

        public async Task<AgentRuntime?> CreateRuntimeAsync(
            Guid agentId,
            AgentExecutionTask task,
            ExecutionSettings settings,
            CancellationToken cancellationToken = default
        )
        {
            var agent = new ChatClientAgent(
                _client,
                new ChatClientAgentOptions { Id = agentId.ToString("D"), Name = "usage" }
            )
                .AsBuilder()
                .Use(runFunc: _telemetry.RunAsync, runStreamingFunc: _telemetry.RunStreamingAsync)
                .Build();
            var session = await agent.CreateSessionAsync(cancellationToken);
            var contextId = ContextIdUtil.ResolveContextId(task.ContextId);
            _sessionState.InitializeSessionState(session, contextId, task.ProjectId);
            return new AgentRuntime(
                NullLogger.Instance,
                agent,
                session,
                task.ProjectId,
                contextId,
                sessionStateScope: null
            );
        }

        public Task<bool> IsRuntimeCurrentAsync(AgentRuntime runtime, CancellationToken cancellationToken = default) =>
            Task.FromResult(!runtime.IsDisposed);

        public Task SetModeAsync(AgentRuntime runtime, string mode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentflowNodeAgent?> CreateAgentflowNodeAgentAsync(
            Guid agentId,
            Guid? projectId,
            Guid conversationId,
            IReadOnlyDictionary<string, string>? environmentVariables,
            bool deferHumanInteractions,
            CancellationToken cancellationToken = default,
            AgwPermissionMode? permissionMode = null
        ) => throw new NotSupportedException();
    }
}

/// <summary>
/// 流式模型调用：推送一次用量后等待取消；取消后等待测试放行才退出，模拟仍在退出中的 worker。
/// A streaming model call: reports usage once and waits for cancellation; after cancellation it exits only when the test releases it, as a worker that is still exiting.
/// </summary>
internal sealed class UsageReportingChatClient : IChatClient
{
    private readonly TaskCompletionSource _usageReported = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public UsageDetails Usage { get; init; } =
        new()
        {
            InputTokenCount = 4,
            OutputTokenCount = 5,
            TotalTokenCount = 9,
        };

    /// <summary>
    /// telemetry 中间件收到用量之后完成。
    /// Completes after the telemetry middleware has received the usage.
    /// </summary>
    public Task UsageReported => _usageReported.Task;

    public void ReleaseExit() => _exit.TrySetResult();

    public void Dispose() { }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException();

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new UsageContent(Usage)] };
        _usageReported.TrySetResult();
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await _exit.Task;
            throw;
        }
    }
}
