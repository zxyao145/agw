using System.Security.Claims;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Agentflows.Runtime;
using Agw.Agents.Execution.Agents.Runners.Durable;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.Messaging.Durable;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Auth.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Execution.Runtimes.Durable;

/// <summary>
/// 执行一个可恢复分段：加载 PostgreSQL 清单、调用 Agent 或 Agentflow，并把普通输出写入消息流。
/// </summary>
internal interface IDurableExecutionSegmentExecutor
{
    Task<DurableExecutionSegmentResult> RunAsync(
        DurableExecutionSegmentInput input,
        CancellationToken cancellationToken
    );

    Task<DurableExecutionSegmentResult> RunAsync(
        DurableExecutionSegmentInput input,
        CancellationToken cancellationToken,
        CancellationToken ownershipLost
    ) => RunAsync(input, cancellationToken);
}

internal sealed class DurableExecutionSegmentExecutor : IDurableExecutionSegmentExecutor
{
    private readonly DurableExecutionStore _store;
    private readonly DurableAgentSegmentRunner _agentRunner;
    private readonly AgentflowRuntimeService _agentflowRuntimeService;
    private readonly IExecutionEventStream _eventStream;
    private readonly ILogger<DurableExecutionSegmentExecutor> _logger;
    private readonly IConversationHistoryPersistence? _historyPersistence;
    private readonly ExecutionEventStreamOptions _eventOptions;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 初始化可执行 Agent 与 Agentflow 的分段执行器。
    /// </summary>
    public DurableExecutionSegmentExecutor(
        DurableExecutionStore store,
        DurableAgentSegmentRunner agentRunner,
        AgentflowRuntimeService agentflowRuntimeService,
        IExecutionEventStream eventStream,
        ILogger<DurableExecutionSegmentExecutor> logger,
        IConversationHistoryPersistence? historyPersistence = null,
        IOptions<ExecutionRuntimeOptions>? options = null,
        TimeProvider? timeProvider = null
    )
    {
        _historyPersistence = historyPersistence;
        _eventOptions = options?.Value.Distributed.EventStream ?? new ExecutionEventStreamOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _store = store;
        _agentRunner = agentRunner;
        _agentflowRuntimeService = agentflowRuntimeService;
        _eventStream = eventStream;
        _logger = logger;
    }

    /// <summary>
    /// 按持久化清单执行指定分段。pending 与 terminal 控制消息由状态落库后的协调层发布。
    /// </summary>
    public Task<DurableExecutionSegmentResult> RunAsync(
        DurableExecutionSegmentInput input,
        CancellationToken cancellationToken
    ) => RunAsync(input, cancellationToken, CancellationToken.None);

    public async Task<DurableExecutionSegmentResult> RunAsync(
        DurableExecutionSegmentInput input,
        CancellationToken cancellationToken,
        CancellationToken ownershipLost
    )
    {
        ArgumentNullException.ThrowIfNull(input);
        DurableExecutionManifest manifest;
        using (UserInfoUtil.PushSystemScope())
        {
            manifest = (await _store.GetAsync(input.ExecutionId, cancellationToken).ConfigureAwait(false)).Manifest;
        }
        using var userScope = UserInfoUtil.Push(CreateUserPrincipal(manifest.ResolveUserId()));
        using var sessionContext = ConversationSessionContext.Push(
            manifest.Task.ProjectId,
            manifest.Task.ContextId,
            manifest.Task.Generation
        );
        await using var historyScope = ConversationHistoryPersistenceContext.BeginScope(
            _historyPersistence,
            manifest.Task.ProjectId,
            manifest.Task.ContextId,
            manifest.Task.Generation,
            ownershipLost
        );
        await using var sink = new ExecutionStreamMessageSink(
            _eventStream,
            input.ExecutionId,
            input.SegmentIndex,
            _logger,
            _eventOptions,
            _timeProvider,
            cancellationToken
        );
        try
        {
            var result = manifest.AgentType switch
            {
                AgentRuntimeType.Agent => await _agentRunner
                    .RunAsync(manifest, input, sink, cancellationToken)
                    .ConfigureAwait(false),
                AgentRuntimeType.Agentflow => await _agentflowRuntimeService
                    .ExecuteDurableSegmentAsync(manifest, input, sink, cancellationToken)
                    .ConfigureAwait(false),
                _ => new DurableExecutionSegmentResult
                {
                    ExecutionId = input.ExecutionId,
                    SegmentIndex = input.SegmentIndex,
                    Status = DurableExecutionSegmentStatus.Failed,
                    ErrorMessage = $"Agent runtime type '{manifest.AgentType}' is not supported.",
                },
            };
            if (result.Status == DurableExecutionSegmentStatus.Failed)
                ConversationHistoryPersistenceContext.RecordFailure(
                    new Agw.Shared.Exceptions.AgwException(
                        Agw.Shared.Exceptions.ErrorCodes.AgentExecutionFailed,
                        result.ErrorMessage ?? "Agent execution failed."
                    )
                );
            return result;
        }
        catch (Exception exception)
        {
            ConversationHistoryPersistenceContext.RecordFailure(exception);
            throw;
        }
    }

    private static ClaimsPrincipal CreateUserPrincipal(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "DurableExecution"));
}
